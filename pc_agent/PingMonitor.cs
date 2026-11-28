using System.Diagnostics;
using System.Net.NetworkInformation;

namespace PCMonitorAgent;

/// <summary>
/// Background ICMP Ping & Jitter monitor + Top-3 RAM-consuming process tracker.
/// </summary>
public sealed class PingMonitor : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<double> _recentPings = new();

    public int? PingMs { get; private set; }
    public int? JitterMs { get; private set; }
    public List<TopProcessItem> TopProcesses { get; private set; } = new();

    public void Start()
    {
        Task.Run(() => PingLoop(_cts.Token));
        Task.Run(() => ProcLoop(_cts.Token));
    }

    private async Task PingLoop(CancellationToken ct)
    {
        using var ping = new Ping();
        var targets = new[] { "1.1.1.1", "8.8.8.8" };
        int tIdx = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var reply = await ping.SendPingAsync(targets[tIdx], 1500);
                if (reply.Status == IPStatus.Success)
                {
                    double ms = reply.RoundtripTime;
                    PingMs = (int)Math.Round(ms);
                    _recentPings.Enqueue(ms);
                    while (_recentPings.Count > 10) _recentPings.Dequeue();

                    if (_recentPings.Count >= 2)
                    {
                        var arr = _recentPings.ToArray();
                        double diffSum = 0;
                        for (int i = 1; i < arr.Length; i++) diffSum += Math.Abs(arr[i] - arr[i - 1]);
                        JitterMs = (int)Math.Round(diffSum / (arr.Length - 1));
                    }
                }
                else
                {
                    tIdx = (tIdx + 1) % targets.Length;
                }
            }
            catch { tIdx = (tIdx + 1) % targets.Length; }

            try { await Task.Delay(2000, ct); } catch { break; }
        }
    }

    private async Task ProcLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var grouped = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        var name = p.ProcessName;
                        if (string.IsNullOrEmpty(name) || name is "Idle" or "System" or "Memory Compression") continue;
                        long mem = p.WorkingSet64;
                        if (grouped.TryGetValue(name, out var prev)) grouped[name] = prev + mem;
                        else grouped[name] = mem;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }

                TopProcesses = grouped
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => new TopProcessItem
                    {
                        Name = kv.Key,
                        RamGb = Math.Round(kv.Value / 1073741824.0, 2)
                    })
                    .ToList();
            }
            catch { }

            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
