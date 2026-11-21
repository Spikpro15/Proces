using System.Text.Json.Serialization;

namespace PCMonitorAgent;

public class AppConfig
{
    [JsonPropertyName("port")]
    public int Port { get; set; } = 8765;

    [JsonPropertyName("refreshMs")]
    public int RefreshMs { get; set; } = 500;

    [JsonPropertyName("enabledMetrics")]
    public List<string> EnabledMetrics { get; set; } = new()
    {
        "fps",
        "cpu_load",
        "cpu_temp",
        "gpu_load",
        "gpu_temp",
        "ram_load"
    };

    public static readonly List<MetricDefinition> AllMetrics = new()
    {
        new("fps", "FPS в игре", "FPS", "Кадры в секунду активной игры (PresentMon)"),
        new("cpu_load", "Процессор", "%", "Общая загрузка процессора"),
        new("cpu_temp", "Темп. CPU", "°C", "Температура процессора (Tctl/Tdie)"),
        new("gpu_load", "Видеокарта", "%", "Загрузка ядра видеокарты"),
        new("gpu_temp", "Темп. GPU", "°C", "Температура чипа видеокарты"),
        new("ram_load", "Оперативная память", "%", "Загрузка ОЗУ в процентах и ГБ"),
        new("gpu_vram", "Видеопамять", "ГБ", "Занятая видеопамять (VRAM)"),
        new("cpu_power", "Питание CPU", "Вт", "Потребление процессора"),
        new("gpu_power", "Питание GPU", "Вт", "Потребление видеокарты"),
        new("cpu_clock", "Частота CPU", "МГц", "Средняя/максимальная частота ядер CPU"),
        new("gpu_clock", "Частота GPU", "МГц", "Частота ядра видеокарты")
    };
}

public record MetricDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("description")] string Description
);

public class TelemetryPayload
{
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("usbConnected")]
    public bool UsbConnected { get; set; }

    [JsonPropertyName("deviceSerial")]
    public string? DeviceSerial { get; set; }

    [JsonPropertyName("presentMonActive")]
    public bool PresentMonActive { get; set; }

    [JsonPropertyName("activeGame")]
    public string? ActiveGame { get; set; }

    [JsonPropertyName("fps")]
    public int? Fps { get; set; }

    [JsonPropertyName("fps1Low")]
    public int? Fps1Low { get; set; }

    [JsonPropertyName("cpuName")]
    public string CpuName { get; set; } = "";

    [JsonPropertyName("gpuName")]
    public string GpuName { get; set; } = "";

    [JsonPropertyName("cpuLoad")]
    public double? CpuLoad { get; set; }

    [JsonPropertyName("cpuTemp")]
    public double? CpuTemp { get; set; }

    [JsonPropertyName("cpuPower")]
    public double? CpuPower { get; set; }

    [JsonPropertyName("cpuClock")]
    public double? CpuClock { get; set; }

    [JsonPropertyName("gpuLoad")]
    public double? GpuLoad { get; set; }

    [JsonPropertyName("gpuTemp")]
    public double? GpuTemp { get; set; }

    [JsonPropertyName("gpuPower")]
    public double? GpuPower { get; set; }

    [JsonPropertyName("gpuClock")]
    public double? GpuClock { get; set; }

    [JsonPropertyName("gpuVramUsedGb")]
    public double? GpuVramUsedGb { get; set; }

    [JsonPropertyName("gpuVramTotalGb")]
    public double? GpuVramTotalGb { get; set; }

    [JsonPropertyName("ramLoad")]
    public double? RamLoad { get; set; }

    [JsonPropertyName("ramUsedGb")]
    public double? RamUsedGb { get; set; }

    [JsonPropertyName("ramTotalGb")]
    public double? RamTotalGb { get; set; }

    [JsonPropertyName("enabledMetrics")]
    public List<string> EnabledMetrics { get; set; } = new();
}
