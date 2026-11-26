using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace PCMonitorAgent;

/// <summary>
/// Free live weather service powered by Open-Meteo (no API key required).
/// Supports manual city name or automatic IP-based city detection.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private readonly ConfigManager _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly CancellationTokenSource _cts = new();
    private string _lastCityQuery = "__init__";

    public WeatherSnapshot? Current { get; private set; }

    public WeatherService(ConfigManager config)
    {
        _config = config;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PCMonitorUSB/9.0");
        _config.ConfigChanged += cfg =>
        {
            var q = (cfg.WeatherCity ?? "").Trim();
            if (!q.Equals(_lastCityQuery, StringComparison.OrdinalIgnoreCase))
            {
                _lastCityQuery = q;
                Task.Run(() => RefreshOnceAsync(_cts.Token));
            }
        };
    }

    public void Start() => Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshOnceAsync(ct);
            try { await Task.Delay(TimeSpan.FromMinutes(15), ct); } catch { break; }
        }
    }

    private async Task RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            var cityQuery = (_config.Current.WeatherCity ?? "").Trim();
            _lastCityQuery = cityQuery;

            double lat, lon;
            string cityName;

            if (!string.IsNullOrEmpty(cityQuery))
            {
                var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(cityQuery)}&count=1&language=ru&format=json";
                var geoJson = await _http.GetStringAsync(geoUrl, ct);
                using var geoDoc = JsonDocument.Parse(geoJson);
                if (!geoDoc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                    return;
                var r0 = results[0];
                lat = r0.GetProperty("latitude").GetDouble();
                lon = r0.GetProperty("longitude").GetDouble();
                cityName = r0.TryGetProperty("name", out var n) ? (n.GetString() ?? cityQuery) : cityQuery;
            }
            else
            {
                var ipJson = await _http.GetStringAsync("http://ip-api.com/json/?lang=ru", ct);
                using var ipDoc = JsonDocument.Parse(ipJson);
                lat = ipDoc.RootElement.GetProperty("lat").GetDouble();
                lon = ipDoc.RootElement.GetProperty("lon").GetDouble();
                cityName = ipDoc.RootElement.TryGetProperty("city", out var c) ? (c.GetString() ?? "") : "";
            }

            var latS = lat.ToString(CultureInfo.InvariantCulture);
            var lonS = lon.ToString(CultureInfo.InvariantCulture);
            var wUrl = $"https://api.open-meteo.com/v1/forecast?latitude={latS}&longitude={lonS}&current=temperature_2m,apparent_temperature,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1";
            var wJson = await _http.GetStringAsync(wUrl, ct);
            using var wDoc = JsonDocument.Parse(wJson);

            var cur = wDoc.RootElement.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            double feels = cur.GetProperty("apparent_temperature").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();

            double? minC = null, maxC = null;
            if (wDoc.RootElement.TryGetProperty("daily", out var daily))
            {
                if (daily.TryGetProperty("temperature_2m_min", out var mn) && mn.GetArrayLength() > 0)
                    minC = Math.Round(mn[0].GetDouble(), 0);
                if (daily.TryGetProperty("temperature_2m_max", out var mx) && mx.GetArrayLength() > 0)
                    maxC = Math.Round(mx[0].GetDouble(), 0);
            }

            var (icon, desc) = DescribeWmoCode(code);
            Current = new WeatherSnapshot
            {
                City = cityName,
                TempC = Math.Round(temp, 0),
                FeelsLikeC = Math.Round(feels, 0),
                MinC = minC,
                MaxC = maxC,
                Desc = desc,
                Icon = icon
            };
        }
        catch { }
    }

    private static (string Icon, string Desc) DescribeWmoCode(int code) => code switch
    {
        0 => ("☀️", "Ясно"),
        1 => ("🌤", "Малооблачно"),
        2 => ("⛅", "Переменная облачность"),
        3 => ("☁️", "Пасмурно"),
        45 or 48 => ("🌫", "Туман"),
        51 or 53 or 55 => ("🌦", "Слабая морось"),
        56 or 57 => ("🌧", "Ледяная морось"),
        61 or 63 => ("🌧", "Дождь"),
        65 => ("🌧", "Ливень"),
        66 or 67 => ("🌧", "Ледяной дождь"),
        71 or 73 or 75 or 77 => ("❄️", "Снег"),
        80 or 81 or 82 => ("🌦", "Ливневый дождь"),
        85 or 86 => ("❄️", "Снегопад"),
        95 or 96 or 99 => ("⛈", "Гроза"),
        _ => ("⛅", "Облачно")
    };

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }
}
