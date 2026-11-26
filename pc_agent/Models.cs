using System.Text.Json.Serialization;

namespace PCMonitorAgent;

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
}

public class AppConfig
{
    [JsonPropertyName("port")] public int Port { get; set; } = 8765;
    [JsonPropertyName("refreshMs")] public int RefreshMs { get; set; } = 500;
    [JsonPropertyName("theme")] public string Theme { get; set; } = "midnight";
    [JsonPropertyName("fontScale")] public double FontScale { get; set; } = 1.0;
    [JsonPropertyName("standbyMode")] public string StandbyMode { get; set; } = "off";
    [JsonPropertyName("weatherCity")] public string WeatherCity { get; set; } = "";
    [JsonPropertyName("obsPort")] public int ObsPort { get; set; } = 4455;
    [JsonPropertyName("obsPassword")] public string ObsPassword { get; set; } = "";
    [JsonPropertyName("layoutCols")] public int LayoutCols { get; set; } = 4;
    [JsonPropertyName("layoutRows")] public int LayoutRows { get; set; } = 3;

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
        new("obs_live", "OBS Статус", "", "Статус эфира/записи и текущая сцена OBS")
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
    [JsonPropertyName("gpuPower")] public double? GpuPower { get; set; }
    [JsonPropertyName("gpuClock")] public double? GpuClock { get; set; }
    [JsonPropertyName("gpuVramUsedGb")] public double? GpuVramUsedGb { get; set; }
    [JsonPropertyName("gpuVramTotalGb")] public double? GpuVramTotalGb { get; set; }
    [JsonPropertyName("ramLoad")] public double? RamLoad { get; set; }
    [JsonPropertyName("ramUsedGb")] public double? RamUsedGb { get; set; }
    [JsonPropertyName("ramTotalGb")] public double? RamTotalGb { get; set; }
    [JsonPropertyName("netUpMbps")] public double? NetUpMbps { get; set; }
    [JsonPropertyName("netDownMbps")] public double? NetDownMbps { get; set; }

    [JsonPropertyName("weather")] public WeatherSnapshot? Weather { get; set; }

    [JsonPropertyName("mediaTitle")] public string? MediaTitle { get; set; }
    [JsonPropertyName("mediaArtist")] public string? MediaArtist { get; set; }
    [JsonPropertyName("mediaPlaying")] public bool MediaPlaying { get; set; }
    [JsonPropertyName("mediaArtVersion")] public int MediaArtVersion { get; set; }
    [JsonPropertyName("mediaPosSec")] public double? MediaPosSec { get; set; }
    [JsonPropertyName("mediaDurSec")] public double? MediaDurSec { get; set; }

    [JsonPropertyName("obsConnected")] public bool ObsConnected { get; set; }
    [JsonPropertyName("obsStreaming")] public bool ObsStreaming { get; set; }
    [JsonPropertyName("obsRecording")] public bool ObsRecording { get; set; }
    [JsonPropertyName("obsStreamTime")] public string? ObsStreamTime { get; set; }
    [JsonPropertyName("obsCurrentScene")] public string? ObsCurrentScene { get; set; }
    [JsonPropertyName("obsScenes")] public List<string> ObsScenes { get; set; } = new();

    [JsonPropertyName("pcApkAvailable")] public bool PcApkAvailable { get; set; }
    [JsonPropertyName("wallpaperVersion")] public int WallpaperVersion { get; set; }
    [JsonPropertyName("standbyMode")] public string StandbyMode { get; set; } = "off";
    [JsonPropertyName("theme")] public string Theme { get; set; } = "midnight";
    [JsonPropertyName("fontScale")] public double FontScale { get; set; } = 1.0;

    [JsonPropertyName("layoutCols")] public int LayoutCols { get; set; } = 4;
    [JsonPropertyName("layoutRows")] public int LayoutRows { get; set; } = 3;
    [JsonPropertyName("layoutBlocks")] public List<LayoutBlock> LayoutBlocks { get; set; } = new();
    [JsonPropertyName("macros")] public List<MacroButton> Macros { get; set; } = new();
}
