using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PCMonitorAgent;

public sealed class FpsTracker : IDisposable
{
    private readonly string _presentMonPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, ProcessFrameBuffer> _buffers = new();
    private Process? _process;

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "dwm.exe", "SearchHost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "TextInputHost.exe", "ApplicationFrameHost.exe",
        "LockApp.exe", "PCMonitorAgent.exe", "Taskmgr.exe", "WindowsTerminal.exe",
        "obs64.exe", "obs32.exe", "Discord.exe"
    };

    public bool IsRunning { get; private set; }

    public FpsTracker(string baseDir)
    {
        _presentMonPath = FindPresentMon(baseDir);
    }

    private static string FindPresentMon(string baseDir)
    {
        var candidates = new[]
        {
            Path.Combine(baseDir, "tools", "PresentMon.exe"),
            Path.Combine(baseDir, "PresentMon.exe")
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        var toolsDir = Path.Combine(baseDir, "tools");
        if (Directory.Exists(toolsDir))
        {
            var found = Directory.GetFiles(toolsDir, "PresentMon*.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (found is not null) return found;
        }
        return candidates[0];
    }

    public void Start()
    {
        if (!File.Exists(_presentMonPath))
        {
            IsRunning = false;
            return;
        }
        Task.Run(() => RunLoop(_cts.Token));
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _presentMonPath,
                    Arguments = "--output_stdout --stop_existing_session --session_name PCMonitorFPS --no_track_input --no_track_gpu",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _process = Process.Start(psi);
                if (_process is null)
                {
                    await Task.Delay(3000, ct);
                    continue;
                }

                IsRunning = true;
                int appIdx = -1, pidIdx = -1, msIdx = -1;

                while (!ct.IsCancellationRequested && !_process.HasExited)
                {
                    var line = await _process.StandardOutput.ReadLineAsync(ct);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var cols = line.Split(',');
                    if (appIdx == -1)
                    {
                        for (int i = 0; i < cols.Length; i++)
                        {
                            var h = cols[i].Trim();
                            if (h.Equals("Application", StringComparison.OrdinalIgnoreCase)) appIdx = i;
                            else if (h.Equals("ProcessID", StringComparison.OrdinalIgnoreCase)) pidIdx = i;
                            else if (h.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase)) msIdx = i;
                        }
                        continue;
                    }

                    if (cols.Length <= Math.Max(appIdx, Math.Max(pidIdx, msIdx))) continue;
                    var appName = cols[appIdx].Trim();
                    if (IgnoredProcesses.Contains(appName)) continue;

                    if (!int.TryParse(cols[pidIdx].Trim(), out int pid)) continue;
                    if (!double.TryParse(cols[msIdx].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)) continue;
                    if (ms <= 0.1 || ms > 2000.0) continue;

                    var buf = _buffers.GetOrAdd(pid, _ => new ProcessFrameBuffer(pid, appName));
                    buf.AddSample(ms);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
            finally
            {
                IsRunning = false;
                TryKillProcess();
            }

            try { await Task.Delay(2500, ct); } catch { break; }
        }
    }

    public (int? Fps, int? Fps1Low, string? GameName, int? ActivePid) GetCurrentFps()
    {
        var now = Environment.TickCount64;

        // Clean processes idle for > 6 seconds (reduces loading-screen flicker)
        foreach (var kv in _buffers)
        {
            if (now - kv.Value.LastTick > 6000)
                _buffers.TryRemove(kv.Key, out _);
        }

        if (_buffers.IsEmpty) return (null, null, null, null);

        int fgPid = GetForegroundPid();
        if (fgPid > 0 && _buffers.TryGetValue(fgPid, out var fgBuf) && now - fgBuf.LastTick <= 5000)
        {
            return fgBuf.ComputeStats();
        }

        var best = _buffers.Values
            .Where(b => now - b.LastTick <= 5000 && !IsBrowser(b.AppName))
            .OrderByDescending(b => b.RecentFrameCount)
            .FirstOrDefault();

        return best is not null ? best.ComputeStats() : (null, null, null, null);
    }

    private static bool IsBrowser(string name) =>
        name.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("opera.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("browser.exe", StringComparison.OrdinalIgnoreCase);

    private void TryKillProcess()
    {
        try
        {
            if (_process is not null && !_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        TryKillProcess();
        _cts.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static int GetForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    private sealed class ProcessFrameBuffer
    {
        private readonly object _lock = new();
        private readonly Queue<(long Tick, double Ms)> _samples = new();
        public int Pid { get; }
        public string AppName { get; }
        public long LastTick { get; private set; }
        public int RecentFrameCount { get { lock (_lock) return _samples.Count; } }

        public ProcessFrameBuffer(int pid, string appName) { Pid = pid; AppName = appName; }

        public void AddSample(double ms)
        {
            var now = Environment.TickCount64;
            lock (_lock)
            {
                _samples.Enqueue((now, ms));
                LastTick = now;
                while (_samples.Count > 0 && now - _samples.Peek().Tick > 1000)
                    _samples.Dequeue();
            }
        }

        public (int? Fps, int? Fps1Low, string? GameName, int? ActivePid) ComputeStats()
        {
            var now = Environment.TickCount64;
            double[] values;
            lock (_lock)
            {
                while (_samples.Count > 0 && now - _samples.Peek().Tick > 1000)
                    _samples.Dequeue();
                if (_samples.Count < 2) return (null, null, Path.GetFileNameWithoutExtension(AppName), Pid);
                values = _samples.Select(s => s.Ms).ToArray();
            }

            double avgMs = values.Average();
            int fps = (int)Math.Round(1000.0 / avgMs);

            Array.Sort(values);
            int idx99 = Math.Clamp((int)(values.Length * 0.99), 0, values.Length - 1);
            int fps1Low = (int)Math.Round(1000.0 / values[idx99]);

            return (fps, fps1Low, Path.GetFileNameWithoutExtension(AppName), Pid);
        }
    }
}
