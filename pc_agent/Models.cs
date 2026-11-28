using System.Text.Json.Serialization;

namespace PCMonitorAgent;

public class TopProcessItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ramGb")] public double RamGb { get; set; }
}

public class AudioAppSession
{
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("volume")] public int Volume { get; set; }
    [JsonPropertyName("muted")] public bool Muted { get; set; }
}

public class MacroButton
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("hotkey")] public string Hotkey { get; set; } = "";
}

public class LayoutBlock
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; } = 1;
    [JsonPropertyName("h")] public int H { get; set; } = 1;
    [JsonPropertyName("color")] public string Color { get; set; } = "#38BDF8";
    [JsonPropertyName("style")] public string Style { get; set; } = "spark";
}

public class AppConfig
{
    [JsonPropertyName("port")] public int Port { get; set; } = 8765;
    [JsonPropertyName("refreshMs")] public int RefreshMs { get; set; } = 500;
    [JsonPropertyName("theme")] public string Theme { get; set; } = "midnight";
    [JsonPropertyName("fontScale")] public double FontScale { get; set; } = 1.0;
    [JsonPropertyName("standbyMode")] public string StandbyMode { get; set; } = "off";
    [JsonPropertyName("autoSwitchModes")] public bool AutoSwitchModes { get; set; } = false;
    [JsonPropertyName("weatherCity")] public string WeatherCity { get; set; } = "";
    [JsonPropertyName("obsPort")] public int ObsPort { get; set; } = 4455;
    [JsonPropertyName("obsPassword")] public string ObsPassword { get; set; } = "";
    [JsonPropertyName("layoutCols")] public int LayoutCols { get; set; } = 4;
    [JsonPropertyName("layoutRows")] public int LayoutRows { get; set; } = 3;
    [JsonPropertyName("activePreset")] public string ActivePreset { get; set; } = "tiktok";

    [JsonPropertyName("layoutBlocks")]
    public List<LayoutBlock> LayoutBlocks { get; set; } = DefaultTikTokLayout();

    [JsonPropertyName("enabledMetrics")]
    public List<string> EnabledMetrics { get; set; } = new()
    {
        "fps", "cpu_load", "cpu_temp", "gpu_load", "gpu_temp", "ram_load"
    };

    [JsonPropertyName("macros")]
    public List<MacroButton> Macros { get; set; } = new()
    {
        new() { Id = "m1", Label = "🎙 Мут мик.", Hotkey = "Ctrl+Shift+M" },
        new() { Id = "m2", Label = "🔴 Запись", Hotkey = "Ctrl+Shift+R" },
        new() { Id = "m3", Label = "🎬 Сцена 1", Hotkey = "Ctrl+Shift+1" },
        new() { Id = "m4", Label = "🎬 Сцена 2", Hotkey = "Ctrl+Shift+2" },
        new() { Id = "m5", Label = "📸 Скриншот", Hotkey = "Win+PrintScreen" },
        new() { Id = "m6", Label = "🔇 Звук ПК", Hotkey = "vol_mute" }
    };

    public static List<LayoutBlock> DefaultTikTokLayout() => new()
    {
        new() { Id = "b1", Type = "clock_weather", X = 0, Y = 0, W = 2, H = 2, Color = "#38BDF8" },
        new() { Id = "b2", Type = "media_player",  X = 2, Y = 0, W = 2, H = 2, Color = "#38BDF8" },
        new() { Id = "b3", Type = "cpu",           X = 0, Y = 2, W = 1, H = 1, Color = "#22D3EE" },
        new() { Id = "b4", Type = "gpu",           X = 1, Y = 2, W = 1, H = 1, Color = "#84CC16" },
        new() { Id = "b5", Type = "ram",           X = 2, Y = 2, W = 1, H = 1, Color = "#A78BFA" },
        new() { Id = "b6", Type = "fps",           X = 3, Y = 2, W = 1, H = 1, Color = "#F59E0B" }
    };


    public static (int Cols, int Rows, List<LayoutBlock> Blocks) GetPreset(string name) => name switch
    {
                        "gauges" => (4, 3, new List<LayoutBlock>
        {
            new() { Id = "rg1", Type = "clock_weather", X = 0, Y = 0, W = 2, H = 2, Color = "#38BDF8", Style = "spark" },
            new() { Id = "rg2", Type = "media_player",  X = 2, Y = 0, W = 2, H = 2, Color = "#38BDF8", Style = "spark" },
            new() { Id = "rg3", Type = "cpu",           X = 0, Y = 2, W = 1, H = 1, Color = "#22D3EE", Style = "gauge" },
            new() { Id = "rg4", Type = "gpu",           X = 1, Y = 2, W = 1, H = 1, Color = "#84CC16", Style = "gauge" },
            new() { Id = "rg5", Type = "ram",           X = 2, Y = 2, W = 1, H = 1, Color = "#A78BFA", Style = "gauge" },
            new() { Id = "rg6", Type = "fps",           X = 3, Y = 2, W = 1, H = 1, Color = "#F59E0B", Style = "gauge" }
        }),
        "oc" => (4, 3, new List<LayoutBlock>
        {
            new() { Id = "o1", Type = "oc_controls", X = 0, Y = 0, W = 2, H = 2, Color = "#F97316" },
            new() { Id = "o2", Type = "gpu_clock",   X = 2, Y = 0, W = 1, H = 1, Color = "#38BDF8" },
            new() { Id = "o3", Type = "gpu_temp",    X = 3, Y = 0, W = 1, H = 1, Color = "#EF4444" },
            new() { Id = "o4", Type = "gpu_power",   X = 2, Y = 1, W = 1, H = 1, Color = "#A855F7" },
            new() { Id = "o5", Type = "gpu_vram",    X = 3, Y = 1, W = 1, H = 1, Color = "#C084FC" },
            new() { Id = "o6", Type = "fps",         X = 0, Y = 2, W = 2, H = 1, Color = "#22C55E" },
            new() { Id = "o7", Type = "gpu",         X = 2, Y = 2, W = 1, H = 1, Color = "#84CC16" },
            new() { Id = "o8", Type = "cpu_temp",    X = 3, Y = 2, W = 1, H = 1, Color = "#F59E0B" }
        }),
        "gamer" => (3, 2, new List<LayoutBlock>
        {
            new() { Id = "g1", Type = "fps",      X = 0, Y = 0, W = 1, H = 1, Color = "#22C55E" },
            new() { Id = "g2", Type = "cpu",      X = 1, Y = 0, W = 1, H = 1, Color = "#22D3EE" },
            new() { Id = "g3", Type = "cpu_temp", X = 2, Y = 0, W = 1, H = 1, Color = "#F43F5E" },
            new() { Id = "g4", Type = "gpu",      X = 0, Y = 1, W = 1, H = 1, Color = "#84CC16" },
            new() { Id = "g5", Type = "gpu_temp", X = 1, Y = 1, W = 1, H = 1, Color = "#10B981" },
            new() { Id = "g6", Type = "ram",      X = 2, Y = 1, W = 1, H = 1, Color = "#A78BFA" }
        }),
        "streamer" => (5, 3, new List<LayoutBlock>
        {
            new() { Id = "s1", Type = "clock_weather", X = 0, Y = 0, W = 2, H = 2, Color = "#38BDF8" },
            new() { Id = "s2", Type = "media_player",  X = 2, Y = 0, W = 2, H = 2, Color = "#38BDF8" },
            new() { Id = "s3", Type = "obs_live",      X = 4, Y = 0, W = 1, H = 2, Color = "#EF4444" },
            new() { Id = "s4", Type = "fps",           X = 0, Y = 2, W = 1, H = 1, Color = "#22C55E" },
            new() { Id = "s5", Type = "cpu",           X = 1, Y = 2, W = 1, H = 1, Color = "#22D3EE" },
            new() { Id = "s6", Type = "gpu",           X = 2, Y = 2, W = 1, H = 1, Color = "#84CC16" },
            new() { Id = "s7", Type = "ram",           X = 3, Y = 2, W = 1, H = 1, Color = "#A78BFA" },
            new() { Id = "s8", Type = "network",       X = 4, Y = 2, W = 1, H = 1, Color = "#FB923C" }
        }),
        _ => (4, 3, new List<LayoutBlock>
        {
            new() { Id = "p1", Type = "clock_weather", X = 0, Y = 0, W = 2, H = 2, Color = "#38BDF8" },
            new() { Id = "p2", Type = "media_player",  X = 2, Y = 0, W = 2, H = 2, Color = "#38BDF8" },
            new() { Id = "p3", Type = "cpu",           X = 0, Y = 2, W = 1, H = 1, Color = "#22D3EE" },
            new() { Id = "p4", Type = "gpu",           X = 1, Y = 2, W = 1, H = 1, Color = "#84CC16" },
            new() { Id = "p5", Type = "ram",           X = 2, Y = 2, W = 1, H = 1, Color = "#A78BFA" },
            new() { Id = "p6", Type = "network",       X = 3, Y = 2, W = 1, H = 1, Color = "#FB923C" }
        })
    };

    public static readonly List<MetricDefinition> AllBlockTypes = new()
    {
        new("clock_weather", "Часы + Погода", "", "Крупные часы, дата, погода и статус ПК/батареи"),
        new("media_player", "Плеер Windows", "", "Обложка, трек, прогресс, кнопки и громкость"),
        new("fps", "FPS в игре", "FPS", "Кадры в секунду и 1% low (PresentMon)"),
        new("cpu", "CPU (% + °C)", "%", "Загрузка процессора + температура и модель"),
        new("gpu", "GPU (% + °C)", "%", "Загрузка видеокарты + температура и VRAM"),
        new("ram", "RAM (ГБ + %)", "ГБ", "Занятая оперативная память и процент"),
        new("network", "Сеть ↑↓", "Мбит", "Скорость отдачи (стрим) и загрузки"),
        new("cpu_temp", "Темп. CPU", "°C", "Отдельная плитка температуры процессора"),
        new("gpu_temp", "Темп. GPU", "°C", "Отдельная плитка температуры видеокарты"),
        new("gpu_vram", "Видеопамять", "ГБ", "Отдельная плитка занятой VRAM"),
        new("cpu_power", "Питание CPU", "Вт", "Потребление процессора в ваттах"),
        new("gpu_power", "Питание GPU", "Вт", "Потребление видеокарты в ваттах"),
        new("cpu_clock", "Частота CPU", "МГц", "Частота ядер процессора"),
        new("gpu_clock", "Частота GPU", "МГц", "Частота ядра видеокарты"),
        new("obs_live", "OBS Статус", "", "Статус эфира/записи и текущая сцена OBS"),
        new("oc_controls", "🔥 Разгон GPU", "", "Кнопки разгона NVIDIA (Сток / Мягкий / Стандарт / Экстрим)"),
        new("gpu_fan", "Кулер GPU", "%", "Обороты вентиляторов видеокарты (% и RPM)"),
        new("gpu_hotspot", "GPU Hotspot", "°C", "Температура самой горячей точки чипа GPU"),
        new("ssd_temp", "Темп. SSD", "°C", "Температура системного NVMe/SSD накопителя"),
        new("ping", "Пинг / Jitter", "мс", "Задержка интернета и джиттер в реальном времени"),
        new("top_procs", "Топ процессов", "ГБ", "Топ-3 процесса по потреблению ОЗУ")
    };
}

public record MetricDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("description")] string Description
);

public class WeatherSnapshot
{
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("tempC")] public double? TempC { get; set; }
    [JsonPropertyName("feelsLikeC")] public double? FeelsLikeC { get; set; }
    [JsonPropertyName("minC")] public double? MinC { get; set; }
    [JsonPropertyName("maxC")] public double? MaxC { get; set; }
    [JsonPropertyName("desc")] public string? Desc { get; set; }
    [JsonPropertyName("icon")] public string? Icon { get; set; }
}

public class TelemetryPayload
{
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("pcName")] public string PcName { get; set; } = "";
    [JsonPropertyName("usbConnected")] public bool UsbConnected { get; set; }
    [JsonPropertyName("deviceSerial")] public string? DeviceSerial { get; set; }
    [JsonPropertyName("presentMonActive")] public bool PresentMonActive { get; set; }
    [JsonPropertyName("activeGame")] public string? ActiveGame { get; set; }
    [JsonPropertyName("gamePlaytime")] public string? GamePlaytime { get; set; }
    [JsonPropertyName("fps")] public int? Fps { get; set; }
    [JsonPropertyName("fps1Low")] public int? Fps1Low { get; set; }

    [JsonPropertyName("cpuName")] public string CpuName { get; set; } = "";
    [JsonPropertyName("gpuName")] public string GpuName { get; set; } = "";
    [JsonPropertyName("cpuLoad")] public double? CpuLoad { get; set; }
    [JsonPropertyName("cpuTemp")] public double? CpuTemp { get; set; }
    [JsonPropertyName("cpuPower")] public double? CpuPower { get; set; }
    [JsonPropertyName("cpuClock")] public double? CpuClock { get; set; }
    [JsonPropertyName("gpuLoad")] public double? GpuLoad { get; set; }
    [JsonPropertyName("gpuTemp")] public double? GpuTemp { get; set; }
    [JsonPropertyName("gpuHotspot")] public double? GpuHotspot { get; set; }
    [JsonPropertyName("gpuFanPct")] public double? GpuFanPct { get; set; }
    [JsonPropertyName("gpuFanRpm")] public double? GpuFanRpm { get; set; }
    [JsonPropertyName("ssdTemp")] public double? SsdTemp { get; set; }
    [JsonPropertyName("gpuPower")] public double? GpuPower { get; set; }
    [JsonPropertyName("gpuClock")] public double? GpuClock { get; set; }
    [JsonPropertyName("gpuVramUsedGb")] public double? GpuVramUsedGb { get; set; }
    [JsonPropertyName("gpuVramTotalGb")] public double? GpuVramTotalGb { get; set; }
    [JsonPropertyName("ramLoad")] public double? RamLoad { get; set; }
    [JsonPropertyName("ramUsedGb")] public double? RamUsedGb { get; set; }
    [JsonPropertyName("ramTotalGb")] public double? RamTotalGb { get; set; }
    [JsonPropertyName("netUpMbps")] public double? NetUpMbps { get; set; }
    [JsonPropertyName("netDownMbps")] public double? NetDownMbps { get; set; }
    [JsonPropertyName("pingMs")] public int? PingMs { get; set; }
    [JsonPropertyName("jitterMs")] public int? JitterMs { get; set; }
    [JsonPropertyName("topProcesses")] public List<TopProcessItem> TopProcesses { get; set; } = new();

    [JsonPropertyName("weather")] public WeatherSnapshot? Weather { get; set; }

    [JsonPropertyName("mediaTitle")] public string? MediaTitle { get; set; }
    [JsonPropertyName("mediaArtist")] public string? MediaArtist { get; set; }
    [JsonPropertyName("mediaPlaying")] public bool MediaPlaying { get; set; }
    [JsonPropertyName("mediaArtVersion")] public int MediaArtVersion { get; set; }
    [JsonPropertyName("mediaPosSec")] public double? MediaPosSec { get; set; }
    [JsonPropertyName("mediaDurSec")] public double? MediaDurSec { get; set; }

        [JsonPropertyName("ocSupported")] public bool OcSupported { get; set; }
    [JsonPropertyName("ocProfile")] public string OcProfile { get; set; } = "stock";
    [JsonPropertyName("ocCoreMhz")] public int OcCoreMhz { get; set; }
    [JsonPropertyName("ocMemMhz")] public int OcMemMhz { get; set; }
    [JsonPropertyName("ocStatus")] public string OcStatus { get; set; } = "";

    [JsonPropertyName("obsConnected")] public bool ObsConnected { get; set; }
    [JsonPropertyName("obsStreaming")] public bool ObsStreaming { get; set; }
    [JsonPropertyName("obsRecording")] public bool ObsRecording { get; set; }
    [JsonPropertyName("obsStreamTime")] public string? ObsStreamTime { get; set; }
    [JsonPropertyName("obsCurrentScene")] public string? ObsCurrentScene { get; set; }
    [JsonPropertyName("obsScenes")] public List<string> ObsScenes { get; set; } = new();

    [JsonPropertyName("pcApkAvailable")] public bool PcApkAvailable { get; set; }
    [JsonPropertyName("wallpaperVersion")] public int WallpaperVersion { get; set; }
    [JsonPropertyName("standbyMode")] public string StandbyMode { get; set; } = "off";
    [JsonPropertyName("autoSwitchModes")] public bool AutoSwitchModes { get; set; } = false;
    [JsonPropertyName("theme")] public string Theme { get; set; } = "midnight";
    [JsonPropertyName("fontScale")] public double FontScale { get; set; } = 1.0;

    [JsonPropertyName("layoutCols")] public int LayoutCols { get; set; } = 4;
    [JsonPropertyName("layoutRows")] public int LayoutRows { get; set; } = 3;
    [JsonPropertyName("activePreset")] public string ActivePreset { get; set; } = "tiktok";
    [JsonPropertyName("layoutBlocks")] public List<LayoutBlock> LayoutBlocks { get; set; } = new();
    [JsonPropertyName("macros")] public List<MacroButton> Macros { get; set; } = new();
    [JsonPropertyName("audioSessions")] public List<AudioAppSession> AudioSessions { get; set; } = new();
}
