using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PCMonitorAgent;

public sealed class TelemetryServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConfigManager _config;
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly CancellationTokenSource _cts = new();
    private string _latestJson = "{}";

    public TelemetryServer(int port, ConfigManager config)
    {
        _config = config;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        Task.Run(() => AcceptLoop(_cts.Token));
    }

    public async Task BroadcastAsync(TelemetryPayload payload)
    {
        _latestJson = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(_latestJson);
        var seg = new ArraySegment<byte>(bytes);

        foreach (var kv in _sockets)
        {
            var ws = kv.Value;
            if (ws.State != WebSocketState.Open)
            {
                _sockets.TryRemove(kv.Key, out _);
                continue;
            }
            try
            {
                await ws.SendAsync(seg, WebSocketMessageType.Text, true, _cts.Token);
            }
            catch
            {
                _sockets.TryRemove(kv.Key, out _);
                try { ws.Dispose(); } catch { }
            }
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx, ct), ct);
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch { }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers.Add("Access-Control-Allow-Origin", "*");
        res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        var path = req.Url?.AbsolutePath ?? "/";

        if (path == "/ws" && req.IsWebSocketRequest)
        {
            await HandleWebSocketAsync(ctx, ct);
            return;
        }

        if (path == "/api/metrics" && req.HttpMethod == "GET")
        {
            await WriteJsonAsync(res, _latestJson);
            return;
        }

        if (path == "/api/config" && req.HttpMethod == "GET")
        {
            var obj = new
            {
                enabledMetrics = _config.Current.EnabledMetrics,
                allMetrics = AppConfig.AllMetrics
            };
            await WriteJsonAsync(res, JsonSerializer.Serialize(obj));
            return;
        }

        if (path == "/api/config" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("enabledMetrics", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    var list = arr.EnumerateArray()
                        .Select(e => e.GetString() ?? "")
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                    _config.UpdateEnabledMetrics(list);
                }
                await WriteJsonAsync(res, "{\"ok\":true}");
            }
            catch
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, "{\"ok\":false}");
            }
            return;
        }

        res.ContentType = "text/html; charset=utf-8";
        var html = Encoding.UTF8.GetBytes(ConfigPageHtml);
        await res.OutputStream.WriteAsync(html, ct);
        res.Close();
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocketContext? wsCtx = null;
        var id = Guid.NewGuid();
        try
        {
            wsCtx = await ctx.AcceptWebSocketAsync(null);
            var ws = wsCtx.WebSocket;
            _sockets[id] = ws;

            var initBytes = Encoding.UTF8.GetBytes(_latestJson);
            await ws.SendAsync(new ArraySegment<byte>(initBytes), WebSocketMessageType.Text, true, ct);

            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch { }
        finally
        {
            _sockets.TryRemove(id, out _);
            try { wsCtx?.WebSocket.Dispose(); } catch { }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, string json)
    {
        res.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        foreach (var ws in _sockets.Values)
            try { ws.Dispose(); } catch { }
        _cts.Dispose();
    }

    private const string ConfigPageHtml = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>PC Monitor USB — Настройки</title>
<style>
  body { background:#0d1117; color:#e6edf3; font-family:system-ui,-apple-system,sans-serif; margin:0; padding:24px; max-width:720px; margin:auto; }
  h1 { font-size:22px; margin:0 0 8px; }
  .sub { color:#8b949e; margin-bottom:20px; font-size:14px; }
  .status { display:flex; gap:12px; margin-bottom:20px; flex-wrap:wrap; }
  .pill { background:#161b22; border:1px solid #30363d; padding:8px 14px; border-radius:999px; font-size:13px; }
  .ok { color:#3fb950; } .warn { color:#d29922; }
  .card { background:#161b22; border:1px solid #30363d; border-radius:12px; padding:16px; }
  .row { display:flex; align-items:center; justify-content:space-between; padding:10px 4px; border-bottom:1px solid #21262d; }
  .row:last-child { border-bottom:none; }
  label { display:flex; align-items:center; gap:12px; cursor:pointer; flex:1; }
  input[type=checkbox] { width:18px; height:18px; accent-color:#58a6ff; }
  .desc { color:#8b949e; font-size:12px; display:block; }
  .val { font-family:ui-monospace,Consolas,monospace; color:#58a6ff; font-size:15px; min-width:90px; text-align:right; }
</style>
</head>
<body>
  <h1>PC Monitor USB</h1>
  <div class="sub">Выбери показатели, которые будут отображаться на экране телефона. Изменения применяются мгновенно.</div>
  <div class="status">
    <div class="pill" id="usb">USB: проверка...</div>
    <div class="pill" id="fpsState">PresentMon: проверка...</div>
  </div>
  <div class="card" id="list">Загрузка...</div>
<script>
let enabled = [];
let allMetrics = [];

async function init() {
  const res = await fetch('/api/config');
  const data = await res.json();
  enabled = data.enabledMetrics;
  allMetrics = data.allMetrics;
  render();
  setInterval(poll, 1000);
  poll();
}

function render() {
  const box = document.getElementById('list');
  box.innerHTML = allMetrics.map(m => `
    <div class="row">
      <label>
        <input type="checkbox" value="${m.id}" ${enabled.includes(m.id) ? 'checked' : ''} onchange="toggle('${m.id}', this.checked)">
        <div><strong>${m.label}</strong><span class="desc">${m.description}</span></div>
      </label>
      <div class="val" id="v_${m.id}">--</div>
    </div>
  `).join('');
}

async function toggle(id, on) {
  if (on && !enabled.includes(id)) enabled.push(id);
  if (!on) enabled = enabled.filter(x => x !== id);
  await fetch('/api/config', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabledMetrics: enabled })
  });
}

async function poll() {
  try {
    const r = await fetch('/api/metrics');
    const d = await r.json();
    document.getElementById('usb').innerHTML = d.usbConnected
      ? '<span class="ok">● USB подключён (' + (d.deviceSerial || 'Android') + ')</span>'
      : '<span class="warn">○ Телефон не найден по USB</span>';
    document.getElementById('fpsState').innerHTML = d.presentMonActive
      ? '<span class="ok">● PresentMon активен</span>'
      : '<span class="warn">○ PresentMon не запущен (запусти setup_tools.bat)</span>';

    const map = {
      fps: d.fps != null ? d.fps + ' FPS' : '--',
      cpu_load: d.cpuLoad != null ? d.cpuLoad + '%' : '--',
      cpu_temp: d.cpuTemp != null ? d.cpuTemp + '°C' : '--',
      gpu_load: d.gpuLoad != null ? d.gpuLoad + '%' : '--',
      gpu_temp: d.gpuTemp != null ? d.gpuTemp + '°C' : '--',
      ram_load: d.ramLoad != null ? d.ramLoad + '% (' + (d.ramUsedGb ?? '?') + ' ГБ)' : '--',
      gpu_vram: d.gpuVramUsedGb != null ? d.gpuVramUsedGb + ' / ' + (d.gpuVramTotalGb ?? '?') + ' ГБ' : '--',
      cpu_power: d.cpuPower != null ? d.cpuPower + ' Вт' : '--',
      gpu_power: d.gpuPower != null ? d.gpuPower + ' Вт' : '--',
      cpu_clock: d.cpuClock != null ? d.cpuClock + ' МГц' : '--',
      gpu_clock: d.gpuClock != null ? d.gpuClock + ' МГц' : '--'
    };
    for (const [k, v] of Object.entries(map)) {
      const el = document.getElementById('v_' + k);
      if (el) el.textContent = v;
    }
  } catch {}
}
init();
</script>
</body>
</html>
""";
}
