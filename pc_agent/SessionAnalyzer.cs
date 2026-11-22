using System.Diagnostics;
using System.Net;
using System.Text;

namespace PCMonitorAgent;

/// <summary>
/// Tracks one continuous gaming session per Windows Process ID (PID) so Alt-Tabs
/// and loading screens never split the session or trigger false FPS drop alerts.
/// Generates a post-session HTML diagnostic report in reports\.
/// </summary>
public sealed class SessionAnalyzer
{
    private readonly string _reportsDir;
    private readonly object _lock = new();

    private int? _activePid;
    private string? _gameName;
    private DateTime _startedAt;
    private DateTime _lastSampleAt = DateTime.MinValue;
    private DateTime _lastDropAt = DateTime.MinValue;

    private int? _candidatePid;
    private string? _candidateName;
    private int _candidateTicks;

    private readonly List<Sample> _samples = new();
    private readonly List<DropEvent> _drops = new();

    public SessionAnalyzer(string baseDir)
    {
        _reportsDir = Path.Combine(baseDir, "reports");
        Directory.CreateDirectory(_reportsDir);
    }

    public string ReportsDir => _reportsDir;

    public void OnTick(TelemetryPayload p, int? presentingPid)
    {
        lock (_lock)
        {
            // 1. Check if active session process has exited
            if (_activePid is not null && !IsProcessAlive(_activePid.Value))
            {
                SaveReportInternal(clearAfter: true);
            }

            // 2. If no active session, wait for 5 seconds of real frames before starting
            if (_activePid is null)
            {
                if (presentingPid is not null && p.Fps is not null && !string.IsNullOrEmpty(p.ActiveGame))
                {
                    if (_candidatePid == presentingPid)
                    {
                        _candidateTicks++;
                        if (_candidateTicks >= 10) // ~5 seconds at 500ms tick
                        {
                            _activePid = presentingPid;
                            _gameName = p.ActiveGame;
                            _startedAt = DateTime.Now;
                            _samples.Clear();
                            _drops.Clear();
                            _candidatePid = null;
                            _candidateTicks = 0;
                        }
                    }
                    else
                    {
                        _candidatePid = presentingPid;
                        _candidateName = p.ActiveGame;
                        _candidateTicks = 1;
                    }
                }
                else
                {
                    _candidatePid = null;
                    _candidateTicks = 0;
                }
                return;
            }

            // 3. Record 1-second sample ONLY while the tracked game is actively presenting
            if (presentingPid != _activePid || p.Fps is null)
                return;

            var now = DateTime.Now;
            if ((now - _lastSampleAt).TotalMilliseconds < 950)
                return;
            _lastSampleAt = now;

            var s = new Sample(
                Time: now,
                Fps: p.Fps.Value,
                Fps1Low: p.Fps1Low ?? p.Fps.Value,
                CpuLoad: p.CpuLoad,
                CpuTemp: p.CpuTemp,
                GpuLoad: p.GpuLoad,
                GpuTemp: p.GpuTemp,
                GpuClock: p.GpuClock,
                VramUsedGb: p.GpuVramUsedGb,
                VramTotalGb: p.GpuVramTotalGb,
                RamLoad: p.RamLoad,
                RamUsedGb: p.RamUsedGb
            );
            _samples.Add(s);

            // 4. Detect real FPS drops after we have a baseline (at least 15 samples)
            if (_samples.Count >= 15 && (now - _lastDropAt).TotalSeconds >= 10)
            {
                var recentBaseline = _samples.Skip(Math.Max(0, _samples.Count - 60)).Average(x => x.Fps);
                bool isFpsDip = s.Fps < recentBaseline * 0.72 && s.Fps < 55;
                bool isMicroStutter = s.Fps1Low < recentBaseline * 0.45 && s.Fps1Low < 32;

                if (isFpsDip || isMicroStutter)
                {
                    _lastDropAt = now;
                    var cause = DiagnoseCause(s);
                    _drops.Add(new DropEvent(now, (int)Math.Round(recentBaseline), s.Fps, s.Fps1Low, cause, s));
                }
            }
        }
    }

    public object GetStatus()
    {
        lock (_lock)
        {
            return new
            {
                active = _activePid is not null,
                gameName = _gameName,
                durationMin = _activePid is not null ? Math.Round((DateTime.Now - _startedAt).TotalMinutes, 1) : 0,
                samplesCount = _samples.Count,
                avgFps = _samples.Count > 0 ? (int)Math.Round(_samples.Average(s => s.Fps)) : (int?)null,
                dropsCount = _drops.Count
            };
        }
    }

    public string? SaveReportNow()
    {
        lock (_lock)
        {
            return SaveReportInternal(clearAfter: false);
        }
    }

    public void FlushOnExit()
    {
        lock (_lock)
        {
            if (_activePid is not null && _samples.Count >= 10)
                SaveReportInternal(clearAfter: true);
        }
    }

    public List<string> ListReports()
    {
        try
        {
            return Directory.GetFiles(_reportsDir, "*.html")
                .OrderByDescending(File.GetLastWriteTime)
                .Take(30)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Cast<string>()
                .ToList();
        }
        catch { return new(); }
    }

    private string? SaveReportInternal(bool clearAfter)
    {
        if (_samples.Count < 5 || string.IsNullOrEmpty(_gameName))
        {
            if (clearAfter) ResetSession();
            return null;
        }

        var safeName = string.Concat(_gameName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var fileName = $"{safeName}_{_startedAt:yyyy-MM-dd_HH-mm}.html";
        var fullPath = Path.Combine(_reportsDir, fileName);

        var html = BuildHtmlReport(_gameName, _startedAt, DateTime.Now, _samples, _drops);
        try { File.WriteAllText(fullPath, html, Encoding.UTF8); } catch { return null; }

        if (clearAfter) ResetSession();
        return fileName;
    }

    private void ResetSession()
    {
        _activePid = null;
        _gameName = null;
        _samples.Clear();
        _drops.Clear();
    }

    private static string DiagnoseCause(Sample s)
    {
        var reasons = new List<string>();

        if (s.VramUsedGb is not null && s.VramTotalGb is not null && s.VramTotalGb > 0 &&
            (s.VramUsedGb.Value / s.VramTotalGb.Value) >= 0.91)
            reasons.Add($"Упор в видеопамять VRAM ({s.VramUsedGb:0.1}/{s.VramTotalGb:0.1} ГБ)");

        if (s.RamLoad is >= 90.0)
            reasons.Add($"Упор в ОЗУ ({s.RamLoad:0}% / {s.RamUsedGb:0.1} ГБ — возможен своп)");

        if (s.GpuTemp is >= 84.0)
            reasons.Add($"Перегрев / троттлинг GPU ({s.GpuTemp:0}°C)");

        if (s.CpuTemp is >= 88.0)
            reasons.Add($"Перегрев / троттлинг CPU ({s.CpuTemp:0}°C)");

        if (s.CpuLoad is >= 92.0)
            reasons.Add($"Упор в процессор (CPU {s.CpuLoad:0}%)");

        if (s.GpuLoad is >= 96.0)
            reasons.Add($"Упор в видеокарту (GPU {s.GpuLoad:0}%)");

        if (reasons.Count == 0)
            reasons.Add("Фриз движка / подгрузка локации или компиляция шейдеров");

        return string.Join(" + ", reasons);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private static string BuildHtmlReport(string game, DateTime start, DateTime end, List<Sample> samples, List<DropEvent> drops)
    {
        int avgFps = (int)Math.Round(samples.Average(s => s.Fps));
        int min1Low = samples.Min(s => s.Fps1Low);
        double maxGpuT = samples.Where(s => s.GpuTemp is not null).Select(s => s.GpuTemp!.Value).DefaultIfEmpty(0).Max();
        double maxCpuT = samples.Where(s => s.CpuTemp is not null && s.CpuTemp > 0).Select(s => s.CpuTemp!.Value).DefaultIfEmpty(0).Max();
        double maxVram = samples.Where(s => s.VramUsedGb is not null).Select(s => s.VramUsedGb!.Value).DefaultIfEmpty(0).Max();
        double maxRam = samples.Where(s => s.RamLoad is not null).Select(s => s.RamLoad!.Value).DefaultIfEmpty(0).Max();

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>Отчёт сеанса — " + WebUtility.HtmlEncode(game) + "</title>");
        sb.AppendLine("<style>body{background:#0d1117;color:#e6edf3;font-family:system-ui,sans-serif;max-width:960px;margin:auto;padding:24px}");
        sb.AppendLine("h1{margin:0 0 6px} .sub{color:#8b949e;margin-bottom:20px;font-size:14px}");
        sb.AppendLine(".grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(145px,1fr));gap:12px;margin-bottom:24px}");
        sb.AppendLine(".card{background:#161b22;border:1px solid #30363d;border-radius:12px;padding:14px} .lbl{color:#8b949e;font-size:12px} .val{font-size:24px;font-weight:700;margin-top:4px;color:#58a6ff}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;background:#161b22;border-radius:12px;overflow:hidden;border:1px solid #30363d}");
        sb.AppendLine("th,td{padding:10px 12px;border-bottom:1px solid #21262d;text-align:left;font-size:13px} th{color:#8b949e;background:#131820} .bad{color:#f85149;font-weight:700}</style></head><body>");
        sb.AppendLine($"<h1>{WebUtility.HtmlEncode(game)} — разбор сеанса</h1>");
        sb.AppendLine($"<div class=\"sub\">{start:dd.MM.yyyy HH:mm} — {end:HH:mm} · активного геймплея: {samples.Count / 60} мин {samples.Count % 60} сек · просадок: {drops.Count}</div>");
        sb.AppendLine("<div class=\"grid\">");
        sb.AppendLine($"<div class=\"card\"><div class=\"lbl\">Средний FPS</div><div class=\"val\">{avgFps}</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"lbl\">Худший 1% Low</div><div class=\"val\">{min1Low}</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"lbl\">Макс. темп. CPU</div><div class=\"val\">{(maxCpuT > 0 ? $"{maxCpuT:0}°C" : "--")}</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"lbl\">Макс. темп. GPU</div><div class=\"val\">{(maxGpuT > 0 ? $"{maxGpuT:0}°C" : "--")}</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"lbl\">Пик VRAM</div><div class=\"val\">{maxVram:0.1} ГБ</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"lbl\">Пик RAM</div><div class=\"val\">{maxRam:0}%</div></div>");
        sb.AppendLine("</div>");

        if (drops.Count == 0)
        {
            sb.AppendLine("<div class=\"card\">Серьёзных просадок FPS за этот сеанс не зафиксировано.</div>");
        }
        else
        {
            sb.AppendLine("<table><thead><tr><th>Время</th><th>FPS (было → стало)</th><th>1% Low</th><th>Железо в этот момент</th><th>Вероятная причина</th></tr></thead><tbody>");
            foreach (var d in drops)
            {
                var hwInfo = $"CPU {d.Snapshot.CpuLoad?.ToString("0") ?? "?"}% / {d.Snapshot.CpuTemp?.ToString("0") ?? "?"}°C · " +
                             $"GPU {d.Snapshot.GpuLoad?.ToString("0") ?? "?"}% / {d.Snapshot.GpuTemp?.ToString("0") ?? "?"}°C · " +
                             $"VRAM {d.Snapshot.VramUsedGb?.ToString("0.1") ?? "?"} ГБ · RAM {d.Snapshot.RamLoad?.ToString("0") ?? "?"}%";
                sb.AppendLine($"<tr><td>{d.Time:HH:mm:ss}</td><td class=\"bad\">{d.BaselineFps} → {d.Fps}</td><td>{d.Fps1Low}</td><td>{hwInfo}</td><td>{WebUtility.HtmlEncode(d.Cause)}</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private record Sample(
        DateTime Time, int Fps, int Fps1Low,
        double? CpuLoad, double? CpuTemp,
        double? GpuLoad, double? GpuTemp, double? GpuClock,
        double? VramUsedGb, double? VramTotalGb,
        double? RamLoad, double? RamUsedGb
    );

    private record DropEvent(DateTime Time, int BaselineFps, int Fps, int Fps1Low, string Cause, Sample Snapshot);
}
