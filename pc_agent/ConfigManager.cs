using System.Text.Json;

namespace PCMonitorAgent;

public class ConfigManager
{
    private readonly string _path;
    private readonly object _lock = new();
    private AppConfig _config;

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
                if (parsed is not null && parsed.EnabledMetrics.Count > 0)
                    return parsed;
            }
        }
        catch { /* fallback to defaults */ }

        var defaults = new AppConfig();
        SaveInternal(defaults);
        return defaults;
    }

    public void UpdateEnabledMetrics(List<string> enabledMetrics)
    {
        var validIds = AppConfig.AllMetrics.Select(m => m.Id).ToHashSet();
        var cleaned = enabledMetrics.Where(id => validIds.Contains(id)).Distinct().ToList();
        if (cleaned.Count == 0)
            cleaned = new AppConfig().EnabledMetrics;

        AppConfig updated;
        lock (_lock)
        {
            _config.EnabledMetrics = cleaned;
            SaveInternal(_config);
            updated = _config;
        }
        ConfigChanged?.Invoke(updated);
    }

    private void SaveInternal(AppConfig cfg)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch { /* ignore write errors */ }
    }
}
