using System.Diagnostics;

namespace PCMonitorAgent;

/// <summary>
/// Watches for a USB-connected Android phone and maintains `adb reverse tcp:PORT tcp:PORT`
/// so the Android APK talks directly to http://127.0.0.1:PORT over the USB cable.
/// </summary>
public sealed class AdbBridge : IDisposable
{
    private readonly string _adbPath;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();

    public bool IsConnected { get; private set; }
    public string? DeviceSerial { get; private set; }
    public bool AdbFound => File.Exists(_adbPath) || _adbPath == "adb";

    public AdbBridge(string baseDir, int port)
    {
        _port = port;
        var bundled = Path.Combine(baseDir, "tools", "platform-tools", "adb.exe");
        var local = Path.Combine(baseDir, "adb.exe");
        _adbPath = File.Exists(bundled) ? bundled : File.Exists(local) ? local : "adb";
    }

    public void Start() => Task.Run(() => WatchLoop(_cts.Token));

    public void ForceReconnect() => Task.Run(CheckAndForwardOnce);

    private async Task WatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await CheckAndForwardOnce();
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    private async Task CheckAndForwardOnce()
    {
        try
        {
            var output = await RunAdbAsync("devices");
            if (output is null)
            {
                IsConnected = false;
                DeviceSerial = null;
                return;
            }

            string? serial = null;
          foreach (var rawLine in output.Split('\n'))
         {
             var line = rawLine.Trim();
             if (line.EndsWith("\tdevice", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains(" device", StringComparison.OrdinalIgnoreCase))
             {
                 var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[1] == "device")
                    {
                        serial = parts[0];
                        break;
                    }
                }
            }

            if (serial is not null)
            {
                await RunAdbAsync($"-s {serial} reverse tcp:{_port} tcp:{_port}");
                IsConnected = true;
                DeviceSerial = serial;
            }
            else
            {
                IsConnected = false;
                DeviceSerial = null;
            }
        }
        catch
        {
            IsConnected = false;
            DeviceSerial = null;
        }
    }

    private async Task<string?> RunAdbAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return stdout;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _cts.Cancel();
}
