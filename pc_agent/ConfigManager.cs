using System.Text.Json;

namespace PCMonitorAgent;

public class ConfigManager
{
    private readonly string _path;
    private readonly object _lock = new();
    private AppConfig _config;

    private static readonly HashSet<string> ValidThemes = new() { "midnight", "amoled", "spide", "cyber" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public event Action<AppConfig>? ConfigChanged;

    public ConfigManager(string baseDir)
    {
        _path = Path.Combine(baseDir, "config.json");
        _config = LoadInternal();
    }

    public AppConfig Current
    {
        get { lock (_lock) return _config; }
    }

    private AppConfig LoadInternal()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (parsed is not null)
                {
                    if (parsed.EnabledMetrics.Count == 0) parsed.EnabledMetrics = new AppConfig().EnabledMetrics;
                    if (parsed.Macros is null || parsed.Macros.Count == 0) parsed.Macros = new AppConfig().Macros;
                    if (!ValidThemes.Contains(parsed.Theme)) parsed.Theme = "midnight";
                    if (parsed.StandbyMode != "auto") parsed.StandbyMode = "off";
                    if (parsed.ObsPort <= 0) parsed.ObsPort = 4455;
                    parsed.FontScale = Math.Clamp(parsed.FontScale, 0.75, 1.45);
                    return parsed;
                }
            }
        }
        catch { }

        var defaults = new AppConfig();
        SaveInternal(defaults);
        return defaults;
    }

    public void UpdateConfig(
        List<string>? enabledMetrics, string? theme, double? fontScale,
        List<MacroButton>? macros, string? standbyMode, int? obsPort, string? obsPassword)
    {
        AppConfig updated;
        lock (_lock)
        {
            if (enabledMetrics is not null)
            {
                var validIds = AppConfig.AllMetrics.Select(m => m.Id).ToHashSet();
                var cleaned = enabledMetrics.Where(id => validIds.Contains(id)).Distinct().ToList();
                if (cleaned.Count > 0) _config.EnabledMetrics = cleaned;
            }
            if (theme is not null && ValidThemes.Contains(theme)) _config.Theme = theme;
            if (fontScale is not null && double.IsFinite(fontScale.Value))
                _config.FontScale = Math.Round(Math.Clamp(fontScale.Value, 0.75, 1.45), 2);
            if (macros is not null && macros.Count > 0) _config.Macros = macros.Take(9).ToList();
            if (standbyMode is "off" or "auto") _config.StandbyMode = standbyMode;
            if (obsPort is > 100 and < 65536) _config.ObsPort = obsPort.Value;
            if (obsPassword is not null) _config.ObsPassword = obsPassword;

            SaveInternal(_config);
            updated = _config;
        }
        ConfigChanged?.Invoke(updated);
    }

    private void SaveInternal(AppConfig cfg)
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(cfg, JsonOpts)); } catch { }
    }
}
