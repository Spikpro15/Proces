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
    private readonly SessionAnalyzer _sessions;
    private readonly MediaController _media;
    private readonly AdbBridge _adb;
    private readonly ObsController _obs;
    private readonly string _apkPath;
    private readonly string _wallpaperPath;
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly CancellationTokenSource _cts = new();
    private string _latestJson = "{}";
    private int _wallpaperVersion;

    public int WallpaperVersion => File.Exists(_wallpaperPath) ? Math.Max(1, _wallpaperVersion) : 0;

    public TelemetryServer(int port, ConfigManager config, SessionAnalyzer sessions, MediaController media, AdbBridge adb, ObsController obs, string baseDir)
    {
        _config = config;
        _sessions = sessions;
        _media = media;
        _adb = adb;
        _obs = obs;
        _apkPath = Path.Combine(baseDir, "PCMonitor-Android.apk");
        _wallpaperPath = Path.Combine(baseDir, "wallpaper.img");
        if (File.Exists(_wallpaperPath)) _wallpaperVersion = 1;
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
            if (ws.State != WebSocketState.Open) { _sockets.TryRemove(kv.Key, out _); continue; }
            try { await ws.SendAsync(seg, WebSocketMessageType.Text, true, _cts.Token); }
            catch { _sockets.TryRemove(kv.Key, out _); try { ws.Dispose(); } catch { } }
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
        res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");

        if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

        var path = req.Url?.AbsolutePath ?? "/";

        if (path == "/ws" && req.IsWebSocketRequest) { await HandleWebSocketAsync(ctx, ct); return; }
        if (path == "/api/metrics" && req.HttpMethod == "GET") { await WriteJsonAsync(res, _latestJson); return; }

        if (path == "/api/update-apk" && req.HttpMethod == "POST")
        {
            var (ok, msg) = await _adb.InstallApkAsync(_apkPath);
            await WriteJsonAsync(res, JsonSerializer.Serialize(new { ok, message = msg }));
            return;
        }

        if (path == "/api/wallpaper" && req.HttpMethod == "GET")
        {
            if (File.Exists(_wallpaperPath))
            {
                res.ContentType = "image/jpeg";
                res.Headers.Add("Cache-Control", "no-store");
                var bytes = await File.ReadAllBytesAsync(_wallpaperPath, ct);
                await res.OutputStream.WriteAsync(bytes, ct);
                res.Close();
            }
            else { res.StatusCode = 204; res.Close(); }
            return;
        }

        if (path == "/api/wallpaper" && req.HttpMethod == "POST")
        {
            using var ms = new MemoryStream();
            await req.InputStream.CopyToAsync(ms, ct);
            var arr = ms.ToArray();
            if (arr.Length > 100 && arr.Length < 10_000_000)
            {
                await File.WriteAllBytesAsync(_wallpaperPath, arr, ct);
                _wallpaperVersion++;
                await WriteJsonAsync(res, "{\"ok\":true}");
            }
            else { res.StatusCode = 400; await WriteJsonAsync(res, "{\"ok\":false}"); }
            return;
        }

        if (path == "/api/wallpaper" && req.HttpMethod == "DELETE")
        {
            try { if (File.Exists(_wallpaperPath)) File.Delete(_wallpaperPath); } catch { }
            _wallpaperVersion++;
            await WriteJsonAsync(res, "{\"ok\":true}");
            return;
        }

        if (path == "/api/obs" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                var scene = doc.RootElement.TryGetProperty("sceneName", out var s) ? s.GetString() : null;
                var ok = await _obs.ExecuteAsync(action, scene);
                await WriteJsonAsync(res, ok ? "{\"ok\":true}" : "{\"ok\":false}");
            }
            catch { res.StatusCode = 400; await WriteJsonAsync(res, "{\"ok\":false}"); }
            return;
        }

        if (path == "/api/media/art" && req.HttpMethod == "GET")
        {
            var art = _media.CurrentArtBytes;
            if (art is not null && art.Length > 0)
            {
                res.ContentType = _media.CurrentArtContentType;
                res.Headers.Add("Cache-Control", "no-store");
                await res.OutputStream.WriteAsync(art, ct);
                res.Close();
            }
            else { res.StatusCode = 204; res.Close(); }
            return;
        }

        if (path == "/api/media" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                var ok = await _media.ExecuteCommandAsync(action);
                await WriteJsonAsync(res, ok ? "{\"ok\":true}" : "{\"ok\":false}");
            }
            catch { res.StatusCode = 400; await WriteJsonAsync(res, "{\"ok\":false}"); }
            return;
        }

        if (path == "/api/macro" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var hotkey = doc.RootElement.TryGetProperty("hotkey", out var h) ? h.GetString() ?? "" : "";
                var ok = MacroController.Trigger(hotkey);
                await WriteJsonAsync(res, ok ? "{\"ok\":true}" : "{\"ok\":false}");
            }
            catch { res.StatusCode = 400; await WriteJsonAsync(res, "{\"ok\":false}"); }
            return;
        }

        if (path == "/api/autostart" && req.HttpMethod == "GET")
        {
            await WriteJsonAsync(res, JsonSerializer.Serialize(new { enabled = AutoStartManager.IsEnabled() }));
            return;
        }

        if (path == "/api/autostart" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(body);
                bool enable = doc.RootElement.TryGetProperty("enabled", out var e) && e.GetBoolean();
                bool ok = AutoStartManager.SetEnabled(enable);
                await WriteJsonAsync(res, JsonSerializer.Serialize(new { ok, enabled = AutoStartManager.IsEnabled() }));
            }
            catch { res.StatusCode = 400; await WriteJsonAsync(res, "{\"ok\":false}"); }
            return;
        }

        if (path == "/api/session" && req.HttpMethod == "GET")
        {
            await WriteJsonAsync(res, JsonSerializer.Serialize(new { session = _sessions.GetStatus(), reports = _sessions.ListReports() }));
            return;
        }

        if (path == "/api/session/save" && req.HttpMethod == "POST")
        {
            var file = _sessions.SaveReportNow();
            await WriteJsonAsync(res, JsonSerializer.Serialize(new { ok = file is not null, file }));
            return;
        }

        if (path.StartsWith("/reports/", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(Uri.UnescapeDataString(path["/reports/".Length..]));
            var fullPath = Path.Combine(_sessions.ReportsDir, fileName);
            if (File.Exists(fullPath))
            {
                res.ContentType = "text/html; charset=utf-8";
                var bytes = await File.ReadAllBytesAsync(fullPath, ct);
                await res.OutputStream.WriteAsync(bytes, ct);
                res.Close();
                return;
            }
            res.StatusCode = 404; res.Close(); return;
        }

        if (path == "/api/config" && req.HttpMethod == "GET")
        {
            var cfg = _config.Current;
            var obj = new
            {
                layoutCols = cfg.LayoutCols,
                layoutRows = cfg.LayoutRows,
                layoutBlocks = cfg.LayoutBlocks,
                theme = cfg.Theme,
                fontScale = cfg.FontScale,
                standbyMode = cfg.StandbyMode,
                weatherCity = cfg.WeatherCity,
                obsPort = cfg.ObsPort,
                obsPassword = cfg.ObsPassword,
                macros = cfg.Macros,
                allBlockTypes = AppConfig.AllBlockTypes
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
                var root = doc.RootElement;

                List<LayoutBlock>? blocks = null;
                if (root.TryGetProperty("layoutBlocks", out var lb) && lb.ValueKind == JsonValueKind.Array)
                    blocks = JsonSerializer.Deserialize<List<LayoutBlock>>(lb.GetRawText());

                int? cols = root.TryGetProperty("layoutCols", out var lc) && lc.ValueKind == JsonValueKind.Number ? lc.GetInt32() : null;
                int? rows = root.TryGetProperty("layoutRows", out var lr) && lr.ValueKind == JsonValueKind.Number ? lr.GetInt32() : null;
                string? theme = root.TryGetProperty("theme", out var th) ? th.GetString() : null;
                double? fontScale = root.TryGetProperty("fontScale", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetDouble() : null;
                string? standby = root.TryGetProperty("standbyMode", out var sb) ? sb.GetString() : null;
                string? city = root.TryGetProperty("weatherCity", out var wc) ? wc.GetString() : null;
                int? obsPort = root.TryGetProperty("obsPort", out var op) && op.ValueKind == JsonValueKind.Number ? op.GetInt32() : null;
                string? obsPass = root.TryGetProperty("obsPassword", out var opw) ? opw.GetString() : null;

                List<MacroButton>? macros = null;
                if (root.TryGetProperty("macros", out var mc) && mc.ValueKind == JsonValueKind.Array)
                    macros = JsonSerializer.Deserialize<List<MacroButton>>(mc.GetRawText());

                _config.UpdateConfig(blocks, cols, rows, theme, fontScale, macros, standby, city, obsPort, obsPass);
                await WriteJsonAsync(res, "{\"ok\":true}");
            }
            catch { res.StatusCode = 400; await WriteJsonAsync(res, "{\"ok\":false}"); }
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
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
        finally { _sockets.TryRemove(id, out _); try { wsCtx?.WebSocket.Dispose(); } catch { } }
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
        foreach (var ws in _sockets.Values) try { ws.Dispose(); } catch { }
        _cts.Dispose();
    }

    private const string ConfigPageHtml = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>PC Monitor USB v9 — Bento-студия экрана</title>
<style>
  * { box-sizing: border-box; }
  body { background:#090d14; color:#e6edf3; font-family:system-ui,-apple-system,sans-serif; margin:0; padding:22px; max-width:1060px; margin:auto; }
  h1 { font-size:22px; margin:0 0 6px; }
  h2 { font-size:15px; margin:20px 0 10px; color:#c9d1d9; }
  .sub { color:#8b949e; margin-bottom:16px; font-size:13px; }
  .topBar { display:flex; gap:9px; align-items:center; flex-wrap:wrap; margin-bottom:16px; }
  .pill { background:#161b22; border:1px solid #30363d; padding:7px 13px; border-radius:999px; font-size:13px; }
  .ok { color:#3fb950; } .warn { color:#d29922; }
  .btn { background:#1e2633; color:#e6edf3; border:1px solid #30363d; border-radius:8px; padding:7px 12px; cursor:pointer; font-size:12px; font-weight:600; }
  .btn:hover { background:#2b3545; }
  .btnGreen { background:#238636; border-color:#2ea043; }
  .btnGreen:hover { background:#2ea043; }
  .btnOn { border-color:#38bdf8; background:#0f2942; color:#38bdf8; }

  .phoneWrap { display:flex; justify-content:center; margin:14px 0 18px; }
  .phoneFrame {
    background:radial-gradient(circle at 20% 20%, #16304a 0%, #0a101b 55%, #070a10 100%);
    background-size:cover; background-position:center;
    border:5px solid #2b3442; border-radius:30px; padding:14px;
    box-shadow:0 20px 50px rgba(0,0,0,0.65);
    width:820px; height:430px; overflow:hidden; display:flex; flex-direction:column; position:relative;
  }
  .bentoCanvas {
    flex:1; min-height:0; height:100%; display:grid; gap:10px; position:relative;
  }
  .cellSlot {
    border:1px dashed rgba(148,163,184,0.22); border-radius:16px;
    display:flex; align-items:center; justify-content:center;
    color:rgba(148,163,184,0.3); font-size:18px; cursor:pointer; transition:all .15s;
  }
  .cellSlot:hover, .cellSlot.over { border-color:#38bdf8; background:rgba(56,189,248,0.12); color:#38bdf8; }
  .bentoBlock {
    background:rgba(15,23,36,0.72); border:1px solid rgba(255,255,255,0.12);
    border-radius:18px; padding:10px 12px; position:relative; overflow:hidden; min-width:0; min-height:0;
    display:flex; flex-direction:column; justify-content:space-between;
    cursor:grab; user-select:none; backdrop-filter:blur(10px);
    transition:transform .12s, box-shadow .12s, border-color .12s;
  }
  .bentoBlock:active { cursor:grabbing; }
  .bentoBlock.selected { border-color:#38bdf8; box-shadow:0 0 0 2px rgba(56,189,248,0.35); }
  .bentoBlock.dragging { opacity:0.38; transform:scale(0.97); }
  .blkTools {
    position:absolute; top:6px; right:6px; display:none; gap:4px; align-items:center;
    background:rgba(9,13,20,0.88); border:1px solid #30363d; border-radius:8px; padding:3px 5px; z-index:5;
  }
  .bentoBlock:hover .blkTools, .bentoBlock.selected .blkTools { display:flex; }
  .tBtn { background:#1e2633; color:#e6edf3; border:none; border-radius:5px; padding:2px 6px; font-size:11px; cursor:pointer; font-weight:700; }
  .tBtn:hover { background:#38bdf8; color:#090d14; }
  .tBtnDel:hover { background:#f85149; color:#fff; }
  .tColor { width:18px; height:18px; border:none; background:none; padding:0; cursor:pointer; }

  .bLbl { font-size:11px; font-weight:800; text-transform:uppercase; letter-spacing:0.5px; }
  .bVal { font-family:ui-monospace,Consolas,monospace; font-size:28px; font-weight:800; color:#fff; line-height:1.1; }
  .bSub { font-size:11px; color:#94a3b8; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .clockBig { font-size:clamp(24px, 3.6vw, 44px); font-weight:300; letter-spacing:-1.5px; line-height:1; color:#fff; }
  .clockSec { font-size:18px; color:#94a3b8; vertical-align:top; margin-left:4px; }
  .spark { width:100%; height:26px; display:block; margin-top:4px; }

  .controlsRow { display:flex; gap:10px; flex-wrap:wrap; align-items:center; background:#141a24; border:1px solid #263042; border-radius:12px; padding:11px 15px; margin-bottom:12px; }
  .chips { display:flex; flex-wrap:wrap; gap:8px; }
  .chip { background:#141a24; border:1px solid #263042; border-radius:10px; padding:7px 12px; cursor:pointer; font-size:12px; font-weight:600; user-select:none; }
  .chip:hover { border-color:#38bdf8; color:#38bdf8; }
  .card { background:#141a24; border:1px solid #263042; border-radius:12px; padding:15px; }
  .row { display:flex; align-items:center; justify-content:space-between; gap:12px; flex-wrap:wrap; }
  .macroGrid { display:grid; grid-template-columns:repeat(auto-fit,minmax(260px,1fr)); gap:10px; }
  .macroRow { display:flex; gap:6px; align-items:center; }
  input[type=text], input[type=password], input[type=number] { background:#0b0f17; border:1px solid #2b3545; color:#e6edf3; border-radius:6px; padding:6px 8px; font-size:12px; width:100%; }
  a { color:#38bdf8; text-decoration:none; }
</style>
</head>
<body>
  <h1>PC Monitor USB v9 — Bento-студия экрана</h1>
  <div class="sub">Наведи на любой блок в мини-телефоне: тяни мышкой на любую клетку, меняй размер (W± / H±), цвет и добавляй новые виджеты. На телефоне обновляется мгновенно.</div>

  <div class="topBar">
    <div class="pill" id="usb">USB: проверка...</div>
    <div class="pill" id="fpsState">PresentMon: проверка...</div>
    <div class="pill" id="obsState">OBS: не подключён</div>
    <button class="btn" id="autoBtn" onclick="toggleAutoStart()">Автозапуск: ...</button>
    <button class="btn btnGreen" id="apkBtn" onclick="updatePhoneApk()">📲 Обновить APK по USB</button>
  </div>

  <div class="controlsRow">
    <strong>Пресеты:</strong>
    <button class="btn" onclick="applyPreset('tiktok')">✨ Стиль TikTok (4×3)</button>
    <button class="btn" onclick="applyPreset('gamer')">🎮 Игровой (3×2)</button>
    <button class="btn" onclick="applyPreset('streamer')">📺 Стример (5×3)</button>
    <div style="margin-left:auto;display:flex;gap:6px;align-items:center">
      <strong>Сетка:</strong>
      <button class="btn" id="g_4x3" onclick="setGrid(4,3)">4×3</button>
      <button class="btn" id="g_5x3" onclick="setGrid(5,3)">5×3</button>
      <button class="btn" id="g_3x2" onclick="setGrid(3,2)">3×2</button>
      <button class="btn" id="g_4x4" onclick="setGrid(4,4)">4×4</button>
    </div>
  </div>

  <div class="phoneWrap">
    <div class="phoneFrame" id="phoneFrame">
      <div class="bentoCanvas" id="bentoCanvas"></div>
    </div>
  </div>

  <h2>Добавить блок на свободное место</h2>
  <div class="chips" id="palette"></div>

  <div class="controlsRow" style="margin-top:14px">
    <strong>Погода (город):</strong>
    <input type="text" id="weatherCity" placeholder="Авто по IP (или впиши, напр. Москва)" style="width:240px">
    <button class="btn btnGreen" onclick="saveWeatherCity()">Применить</button>
    <div style="margin-left:auto;display:flex;gap:8px;align-items:center">
      <input type="file" id="bgFile" accept="image/*" style="display:none" onchange="uploadBg(this)">
      <button class="btn" onclick="document.getElementById('bgFile').click()">🖼 Свой фон</button>
      <button class="btn" onclick="clearBg()">Убрать фон</button>
      <button class="btn" id="standbyBtn" onclick="toggleStandby()">Часы вне игры: Выкл</button>
    </div>
  </div>

  <div class="controlsRow">
    <strong>Тема:</strong>
    <button class="btn" id="th_midnight" onclick="setTheme('midnight')">Тёмная</button>
    <button class="btn" id="th_amoled" onclick="setTheme('amoled')">AMOLED</button>
    <button class="btn" id="th_spide" onclick="setTheme('spide')">Тёплая Spide</button>
    <button class="btn" id="th_cyber" onclick="setTheme('cyber')">Киберпанк</button>
    <div style="margin-left:auto;display:flex;align-items:center;gap:8px">
      <strong>Шрифт:</strong>
      <input type="range" id="fontSlider" min="0.8" max="1.35" step="0.05" value="1.0" oninput="setFontScale(this.value)">
      <span id="fontVal" style="font-size:12px;color:#8b949e;min-width:38px">100%</span>
    </div>
  </div>

  <h2>OBS Studio WebSocket</h2>
  <div class="card">
    <div class="row">
      <div style="display:flex;gap:8px;align-items:center;flex:1;min-width:260px">
        <span style="font-size:12px;color:#8b949e">Порт:</span>
        <input type="number" id="obsPort" value="4455" style="width:90px">
        <span style="font-size:12px;color:#8b949e">Пароль:</span>
        <input type="password" id="obsPass" placeholder="Пусто, если без пароля">
      </div>
      <button class="btn btnGreen" onclick="saveConfig()">Сохранить OBS</button>
    </div>
  </div>

  <h2>Стримерские кнопки (свайп влево на телефоне)</h2>
  <div class="card">
    <div class="macroGrid" id="macroList"></div>
    <div style="margin-top:12px;display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px">
      <span style="font-size:12px;color:#8b949e">Клавиши: Ctrl+Shift+M, Alt+F9, Win+PrintScreen, F13, vol_mute</span>
      <button class="btn btnGreen" onclick="saveMacros()">Сохранить кнопки</button>
    </div>
  </div>

  <h2>Игровой сеанс и отчёты просадок</h2>
  <div class="card">
    <div class="row">
      <div id="sessInfo">Нет активной игры — запусти игру, запись начнётся автоматически.</div>
      <button class="btn btnGreen" onclick="saveReport()">Сохранить отчёт сейчас</button>
    </div>
    <div id="reportsList" style="padding-top:10px;font-size:13px;color:#8b949e">Сохранённых отчётов пока нет.</div>
  </div>

<script>
const DEFAULT_COLORS = {
  clock_weather:'#38BDF8', media_player:'#38BDF8', fps:'#F59E0B', cpu:'#22D3EE',
  gpu:'#84CC16', ram:'#A78BFA', network:'#FB923C', cpu_temp:'#F43F5E',
  gpu_temp:'#10B981', gpu_vram:'#C084FC', cpu_power:'#60A5FA', gpu_power:'#E879F9',
  cpu_clock:'#38BDF8', gpu_clock:'#A855F7', obs_live:'#EF4444'
};

let layoutCols = 4, layoutRows = 3, layoutBlocks = [], allBlockTypes = [], macros = [], latestData = {};
let theme = 'midnight', fontScale = 1.0, standbyMode = 'off', dragBlockId = null, lastBgVer = -1, autoStartOn = false;
const history = {};

async function init() {
  const res = await fetch('/api/config');
  const data = await res.json();
  layoutCols = data.layoutCols || 4;
  layoutRows = data.layoutRows || 3;
  layoutBlocks = data.layoutBlocks || [];
  allBlockTypes = data.allBlockTypes || [];
  theme = data.theme || 'midnight';
  fontScale = data.fontScale || 1.0;
  standbyMode = data.standbyMode || 'off';
  macros = data.macros || [];

  document.getElementById('weatherCity').value = data.weatherCity || '';
  document.getElementById('fontSlider').value = fontScale;
  document.getElementById('fontVal').textContent = Math.round(fontScale * 100) + '%';
  document.getElementById('obsPort').value = data.obsPort || 4455;
  document.getElementById('obsPass').value = data.obsPassword || '';

  updateStandbyBtn(); renderPalette(); renderCanvas(); renderMacros(); pollAutoStart();
  setInterval(poll, 800); setInterval(pollSession, 3000);
  poll(); pollSession();
}

function overlaps(a, b) {
  return a.x < b.x + b.w && a.x + a.w > b.x && a.y < b.y + b.h && a.y + a.h > b.y;
}

function canPlace(testBlk, ignoreId) {
  if (testBlk.x < 0 || testBlk.y < 0 || testBlk.x + testBlk.w > layoutCols || testBlk.y + testBlk.h > layoutRows) return false;
  return !layoutBlocks.some(b => b.id !== ignoreId && overlaps(testBlk, b));
}

function findFreeSpot(w, h) {
  for (let y = 0; y <= layoutRows - h; y++)
    for (let x = 0; x <= layoutCols - w; x++)
      if (canPlace({ x, y, w, h }, null)) return { x, y };
  return null;
}

function renderPalette() {
  document.getElementById('palette').innerHTML = allBlockTypes.map(t =>
    `<div class="chip" onclick="addBlock('${t.id}')">+ ${t.label}</div>`
  ).join('');
}

function addBlock(type) {
  const prefW = (type === 'clock_weather' || type === 'media_player') ? 2 : 1;
  const prefH = (type === 'clock_weather' || type === 'media_player') ? 2 : 1;
  let spot = findFreeSpot(prefW, prefH);
  let w = prefW, h = prefH;
  if (!spot) { spot = findFreeSpot(1, 1); w = 1; h = 1; }
  if (!spot) { alert('Нет свободных клеток в сетке! Удали лишний блок или увеличь сетку (например, 5×3).'); return; }
  layoutBlocks.push({
    id: 'b_' + Date.now() + '_' + Math.floor(Math.random()*999),
    type, x: spot.x, y: spot.y, w, h, color: DEFAULT_COLORS[type] || '#38BDF8'
  });
  renderCanvas(); saveConfig();
}

function removeBlock(id) {
  if (layoutBlocks.length <= 1) return;
  layoutBlocks = layoutBlocks.filter(b => b.id !== id);
  renderCanvas(); saveConfig();
}

function resizeBlock(id, dw, dh) {
  const b = layoutBlocks.find(x => x.id === id);
  if (!b) return;
  const nw = Math.max(1, Math.min(layoutCols, b.w + dw));
  const nh = Math.max(1, Math.min(layoutRows, b.h + dh));
  if (canPlace({ ...b, w: nw, h: nh }, b.id)) {
    b.w = nw; b.h = nh;
    renderCanvas(); saveConfig();
  }
}

function recolorBlock(id, color) {
  const b = layoutBlocks.find(x => x.id === id);
  if (!b) return;
  b.color = color;
  renderCanvas(); saveConfig();
}

function setGrid(c, r) {
  layoutCols = c; layoutRows = r;
  layoutBlocks = layoutBlocks.filter(b => b.x < c && b.y < r).map(b => ({
    ...b, w: Math.min(b.w, c - b.x), h: Math.min(b.h, r - b.y)
  }));
  renderCanvas(); saveConfig();
}

function applyPreset(name) {
  if (name === 'tiktok') {
    layoutCols = 4; layoutRows = 3;
    layoutBlocks = [
      { id:'p1', type:'clock_weather', x:0, y:0, w:2, h:2, color:'#38BDF8' },
      { id:'p2', type:'media_player',  x:2, y:0, w:2, h:2, color:'#38BDF8' },
      { id:'p3', type:'cpu',           x:0, y:2, w:1, h:1, color:'#22D3EE' },
      { id:'p4', type:'gpu',           x:1, y:2, w:1, h:1, color:'#84CC16' },
      { id:'p5', type:'ram',           x:2, y:2, w:1, h:1, color:'#A78BFA' },
      { id:'p6', type:'network',       x:3, y:2, w:1, h:1, color:'#FB923C' }
    ];
  } else if (name === 'gamer') {
    layoutCols = 3; layoutRows = 2;
    layoutBlocks = [
      { id:'g1', type:'fps',      x:0, y:0, w:1, h:1, color:'#22C55E' },
      { id:'g2', type:'cpu',      x:1, y:0, w:1, h:1, color:'#22D3EE' },
      { id:'g3', type:'cpu_temp', x:2, y:0, w:1, h:1, color:'#F43F5E' },
      { id:'g4', type:'gpu',      x:0, y:1, w:1, h:1, color:'#84CC16' },
      { id:'g5', type:'gpu_temp', x:1, y:1, w:1, h:1, color:'#10B981' },
      { id:'g6', type:'ram',      x:2, y:1, w:1, h:1, color:'#A78BFA' }
    ];
  } else if (name === 'streamer') {
    layoutCols = 5; layoutRows = 3;
    layoutBlocks = [
      { id:'s1', type:'clock_weather', x:0, y:0, w:2, h:2, color:'#38BDF8' },
      { id:'s2', type:'media_player',  x:2, y:0, w:2, h:2, color:'#38BDF8' },
      { id:'s3', type:'obs_live',      x:4, y:0, w:1, h:2, color:'#EF4444' },
      { id:'s4', type:'fps',           x:0, y:2, w:1, h:1, color:'#22C55E' },
      { id:'s5', type:'cpu',           x:1, y:2, w:1, h:1, color:'#22D3EE' },
      { id:'s6', type:'gpu',           x:2, y:2, w:1, h:1, color:'#84CC16' },
      { id:'s7', type:'ram',           x:3, y:2, w:1, h:1, color:'#A78BFA' },
      { id:'s8', type:'network',       x:4, y:2, w:1, h:1, color:'#FB923C' }
    ];
  }
  renderCanvas(); saveConfig();
}

function renderCanvas() {
  const cv = document.getElementById('bentoCanvas');
  cv.style.gridTemplateColumns = `repeat(${layoutCols}, minmax(0, 1fr))`;
  cv.style.gridTemplateRows = `repeat(${layoutRows}, minmax(0, 1fr))`;

  for (const g of ['4x3','5x3','3x2','4x4']) {
    const el = document.getElementById('g_' + g);
    if (el) el.classList.toggle('btnOn', g === `${layoutCols}x${layoutRows}`);
  }

  let html = '';
  // Background drop slots for empty cells
  for (let y = 0; y < layoutRows; y++) {
    for (let x = 0; x < layoutCols; x++) {
      const occ = layoutBlocks.some(b => x >= b.x && x < b.x + b.w && y >= b.y && y < b.y + b.h);
      if (!occ) {
        html += `<div class="cellSlot" style="grid-column:${x+1};grid-row:${y+1}"
                      ondragover="event.preventDefault();this.classList.add('over')"
                      ondragleave="this.classList.remove('over')"
                      ondrop="onDropCell(event,${x},${y})">+</div>`;
      }
    }
  }

  // Blocks
  for (const b of layoutBlocks) {
    const meta = allBlockTypes.find(t => t.id === b.type) || { label: b.type };
    const grad = `linear-gradient(145deg, ${b.color}22 0%, rgba(15,23,36,0.78) 65%)`;
    html += `
      <div class="bentoBlock" draggable="true" id="blk_${b.id}"
           style="grid-column:${b.x+1} / span ${b.w}; grid-row:${b.y+1} / span ${b.h}; background:${grad}; border-color:${b.color}55"
           ondragstart="onDragBlock(event,'${b.id}')"
           ondragover="event.preventDefault()"
           ondrop="onDropOnBlock(event,'${b.id}')"
           ondragend="this.classList.remove('dragging')">
        <div class="blkTools" onclick="event.stopPropagation()">
          <input type="color" class="tColor" value="${b.color}" title="Цвет" onchange="recolorBlock('${b.id}',this.value)">
          <button class="tBtn" title="Узже" onclick="resizeBlock('${b.id}',-1,0)">W-</button>
          <button class="tBtn" title="Шире" onclick="resizeBlock('${b.id}',1,0)">W+</button>
          <button class="tBtn" title="Ниже" onclick="resizeBlock('${b.id}',0,-1)">H-</button>
          <button class="tBtn" title="Выше" onclick="resizeBlock('${b.id}',0,1)">H+</button>
          <button class="tBtn tBtnDel" title="Удалить" onclick="removeBlock('${b.id}')">×</button>
        </div>
        <div id="bc_${b.id}" style="height:100%;display:flex;flex-direction:column;justify-content:space-between">
          <div class="bLbl" style="color:${b.color}">${meta.label}</div>
          <div class="bVal">--</div>
          <div class="bSub"></div>
        </div>
      </div>`;
  }
  cv.innerHTML = html;
  updateValues();
}

function onDragBlock(e, id) { dragBlockId = id; e.currentTarget.classList.add('dragging'); }

function onDropCell(e, tx, ty) {
  e.preventDefault(); e.currentTarget.classList.remove('over');
  const b = layoutBlocks.find(x => x.id === dragBlockId);
  if (!b) return;
  if (canPlace({ ...b, x: tx, y: ty }, b.id)) {
    b.x = tx; b.y = ty;
    renderCanvas(); saveConfig();
  }
  dragBlockId = null;
}

function onDropOnBlock(e, targetId) {
  e.preventDefault();
  if (!dragBlockId || dragBlockId === targetId) return;
  const a = layoutBlocks.find(x => x.id === dragBlockId);
  const b = layoutBlocks.find(x => x.id === targetId);
  if (!a || !b) return;
  if (a.w === b.w && a.h === b.h) {
    const ax = a.x, ay = a.y;
    a.x = b.x; a.y = b.y; b.x = ax; b.y = ay;
    renderCanvas(); saveConfig();
  }
  dragBlockId = null;
}

function updateValues() {
  const d = latestData;
  if (!d || Object.keys(d).length === 0) return;

  document.getElementById('usb').innerHTML = d.usbConnected ? '<span class="ok">● USB подключён (' + (d.deviceSerial || 'Android') + ')</span>' : '<span class="warn">○ Телефон не найден по USB</span>';
  document.getElementById('fpsState').innerHTML = d.presentMonActive ? '<span class="ok">● PresentMon активен</span>' : '<span class="warn">○ PresentMon не запущен</span>';
  document.getElementById('obsState').innerHTML = d.obsConnected ? '<span class="ok">● OBS подключён</span>' : '<span class="warn">○ OBS не подключён</span>';

  if (d.wallpaperVersion !== lastBgVer) {
    lastBgVer = d.wallpaperVersion;
    const pf = document.getElementById('phoneFrame');
    pf.style.backgroundImage = lastBgVer > 0
      ? `linear-gradient(rgba(9,12,16,0.45),rgba(9,12,16,0.65)), url('/api/wallpaper?v=${lastBgVer}')`
      : '';
  }

  const now = new Date();
  const hhmm = now.toTimeString().slice(0,5);
  const ss = now.toTimeString().slice(6,8);
  const dateStr = now.toLocaleDateString('ru-RU', { weekday:'long', day:'numeric', month:'long' });

  for (const b of layoutBlocks) {
    const el = document.getElementById('bc_' + b.id);
    if (!el) continue;

    if (b.type === 'clock_weather') {
      const w = d.weather;
      const wLine = w && w.tempC != null
        ? `${w.icon || '⛅'} <strong>${Math.round(w.tempC)}°</strong> ${w.desc || ''} · ${w.city || ''}<div class="bSub">↑${w.maxC ?? '?'}° ↓${w.minC ?? '?'}° · Ощущается как ${w.feelsLikeC ?? '?'}°</div>`
        : `<div class="bSub">Загрузка погоды...</div>`;
      el.innerHTML = `
        <div class="bSub" style="color:#3fb950">● ${d.pcName || 'PC'} · USB</div>
        <div><span class="clockBig">${hhmm}</span><span class="clockSec">${ss}</span></div>
        <div class="bSub">${dateStr}</div>
        <div style="font-size:13px;margin-top:4px">${wLine}</div>`;
    } else if (b.type === 'media_player') {
      const title = d.mediaTitle || 'Ничего не играет';
      const artist = d.mediaArtist || 'Включи музыку на ПК';
      el.innerHTML = `
        <div class="bLbl" style="color:${b.color}">MEDIA</div>
        <div style="font-size:16px;font-weight:700;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${title}</div>
        <div class="bSub">${artist}</div>
        <div style="display:flex;gap:8px;margin-top:8px">
          <button class="btn" onclick="event.stopPropagation();mediaCmd('prev')">⏮</button>
          <button class="btn btnOn" onclick="event.stopPropagation();mediaCmd('play_pause')">${d.mediaPlaying ? '⏸' : '▶'}</button>
          <button class="btn" onclick="event.stopPropagation();mediaCmd('next')">⏭</button>
        </div>`;
    } else {
      let lbl = b.type.toUpperCase(), val = '--', sub = '', num = null, maxV = 100;
      if (b.type === 'cpu') { lbl='CPU'; val = d.cpuLoad != null ? Math.round(d.cpuLoad)+'%' : '--'; sub = (d.cpuTemp != null ? Math.round(d.cpuTemp)+'°C · ' : '') + (d.cpuName || ''); num = d.cpuLoad; }
      else if (b.type === 'gpu') { lbl='GPU'; val = d.gpuLoad != null ? Math.round(d.gpuLoad)+'%' : '--'; sub = (d.gpuTemp != null ? Math.round(d.gpuTemp)+'°C · ' : '') + (d.gpuVramUsedGb != null ? d.gpuVramUsedGb.toFixed(1)+' ГБ' : ''); num = d.gpuLoad; }
      else if (b.type === 'ram') { lbl='RAM' + (d.ramLoad != null ? ` (${Math.round(d.ramLoad)}%)` : ''); val = d.ramUsedGb != null ? d.ramUsedGb.toFixed(1)+' ГБ' : '--'; sub = d.ramTotalGb != null ? `из ${d.ramTotalGb.toFixed(0)} ГБ` : ''; num = d.ramLoad; }
      else if (b.type === 'fps') { lbl='FPS'; val = d.fps != null ? d.fps : '--'; sub = d.fps1Low ? `1% low: ${d.fps1Low}` : (d.activeGame || 'Нет игры'); num = d.fps; maxV = 165; }
      else if (b.type === 'network') { lbl='NETWORK'; val = d.netUpMbps != null ? '↑'+d.netUpMbps.toFixed(1) : '--'; sub = d.netDownMbps != null ? `↓ ${d.netDownMbps.toFixed(1)} Мбит/с` : ''; num = d.netUpMbps; maxV = 50; }
      else if (b.type === 'cpu_temp') { lbl='CPU TEMP'; val = d.cpuTemp != null ? Math.round(d.cpuTemp)+'°C' : '--'; sub = 'Tctl / Package'; num = d.cpuTemp; }
      else if (b.type === 'gpu_temp') { lbl='GPU TEMP'; val = d.gpuTemp != null ? Math.round(d.gpuTemp)+'°C' : '--'; sub = 'GPU Core'; num = d.gpuTemp; }
      else if (b.type === 'gpu_vram') { lbl='VRAM'; val = d.gpuVramUsedGb != null ? d.gpuVramUsedGb.toFixed(1)+' ГБ' : '--'; sub = d.gpuVramTotalGb ? `из ${d.gpuVramTotalGb.toFixed(1)} ГБ` : ''; num = d.gpuVramUsedGb; maxV = d.gpuVramTotalGb || 8; }
      else if (b.type === 'cpu_power') { lbl='CPU PWR'; val = d.cpuPower != null ? Math.round(d.cpuPower)+' Вт' : '--'; num = d.cpuPower; maxV = 120; }
      else if (b.type === 'gpu_power') { lbl='GPU PWR'; val = d.gpuPower != null ? Math.round(d.gpuPower)+' Вт' : '--'; num = d.gpuPower; maxV = 200; }
      else if (b.type === 'cpu_clock') { lbl='CPU CLK'; val = d.cpuClock != null ? Math.round(d.cpuClock)+' МГц' : '--'; num = d.cpuClock; maxV = 5000; }
      else if (b.type === 'gpu_clock') { lbl='GPU CLK'; val = d.gpuClock != null ? Math.round(d.gpuClock)+' МГц' : '--'; num = d.gpuClock; maxV = 3000; }
      else if (b.type === 'obs_live') { lbl='OBS'; val = d.obsStreaming ? 'LIVE' : (d.obsConnected ? 'READY' : 'OFF'); sub = d.obsStreamTime || d.obsCurrentScene || ''; }

      let sparkSvg = '';
      if (num != null && !isNaN(num)) {
        if (!history[b.id]) history[b.id] = [];
        history[b.id].push(Math.max(0, Math.min(1, num / maxV)));
        if (history[b.id].length > 30) history[b.id].shift();
        if (history[b.id].length >= 2) {
          const pts = history[b.id].map((v, i) => `${(i / (history[b.id].length - 1) * 100).toFixed(1)},${(24 - v * 21).toFixed(1)}`).join(' ');
          sparkSvg = `<svg class="spark" viewBox="0 0 100 26" preserveAspectRatio="none"><polyline fill="none" stroke="${b.color}" stroke-width="2" points="${pts}"/></svg>`;
        }
      }
      el.innerHTML = `<div class="bLbl" style="color:${b.color}">${lbl}</div><div class="bVal">${val}</div><div class="bSub">${sub}</div>${sparkSvg}`;
    }
  }
}

async function saveWeatherCity() { await saveConfig(); setTimeout(poll, 1200); }
function setTheme(t) { theme = t; saveConfig(); }
function setFontScale(v) { fontScale = parseFloat(v); document.getElementById('fontVal').textContent = Math.round(fontScale*100)+'%'; saveConfig(); }
function updateStandbyBtn() { const b = document.getElementById('standbyBtn'); b.textContent = standbyMode === 'auto' ? '✓ Часы вне игры: Авто' : 'Часы вне игры: Выкл'; b.classList.toggle('btnOn', standbyMode === 'auto'); }
async function toggleStandby() { standbyMode = standbyMode === 'auto' ? 'off' : 'auto'; updateStandbyBtn(); await saveConfig(); }
async function uploadBg(input) { if (!input.files || !input.files[0]) return; await fetch('/api/wallpaper', { method:'POST', body: input.files[0] }); input.value=''; setTimeout(poll, 250); }
async function clearBg() { await fetch('/api/wallpaper', { method:'DELETE' }); setTimeout(poll, 250); }
async function mediaCmd(action) { await fetch('/api/media', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ action }) }); setTimeout(poll, 250); }

function renderMacros() {
  document.getElementById('macroList').innerHTML = macros.map((m, i) => `
    <div class="macroRow">
      <input type="text" id="ml_${i}" value="${m.label}" placeholder="Название">
      <input type="text" id="mh_${i}" value="${m.hotkey}" placeholder="Клавиши">
      <button class="btn" onclick="testMacro(${i})">▶</button>
    </div>`).join('');
}
async function saveMacros() {
  macros = macros.map((m, i) => ({ id: m.id, label: document.getElementById('ml_'+i).value.trim() || m.label, hotkey: document.getElementById('mh_'+i).value.trim() || m.hotkey }));
  await saveConfig();
}
async function testMacro(i) { await fetch('/api/macro', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ hotkey: document.getElementById('mh_'+i).value.trim() }) }); }

async function saveConfig() {
  await fetch('/api/config', {
    method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({
      layoutCols, layoutRows, layoutBlocks, theme, fontScale, standbyMode, macros,
      weatherCity: document.getElementById('weatherCity').value.trim(),
      obsPort: parseInt(document.getElementById('obsPort').value || '4455', 10),
      obsPassword: document.getElementById('obsPass').value || ''
    })
  });
}

async function updatePhoneApk() {
  const b = document.getElementById('apkBtn');
  b.disabled = true; b.textContent = '⏳ Устанавливаю...';
  try {
    const r = await fetch('/api/update-apk', { method:'POST' });
    const d = await r.json();
    b.textContent = d.ok ? '✓ Обновлено!' : ('✕ ' + (d.message || 'Ошибка'));
  } catch { b.textContent = '✕ Ошибка связи'; }
  setTimeout(() => { b.disabled = false; b.textContent = '📲 Обновить APK по USB'; }, 3500);
}

async function pollAutoStart() {
  try {
    const r = await fetch('/api/autostart'); const d = await r.json();
    autoStartOn = !!d.enabled;
    const b = document.getElementById('autoBtn');
    b.textContent = autoStartOn ? '✓ Автозапуск: Вкл' : 'Автозапуск: Выкл';
    b.classList.toggle('btnOn', autoStartOn);
  } catch {}
}
async function toggleAutoStart() {
  const r = await fetch('/api/autostart', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ enabled: !autoStartOn }) });
  const d = await r.json(); autoStartOn = !!d.enabled;
  const b = document.getElementById('autoBtn');
  b.textContent = autoStartOn ? '✓ Автозапуск: Вкл' : 'Автозапуск: Выкл';
  b.classList.toggle('btnOn', autoStartOn);
}

async function saveReport() {
  const r = await fetch('/api/session/save', { method:'POST' });
  const d = await r.json(); await pollSession();
  if (d.ok && d.file) window.open('/reports/' + encodeURIComponent(d.file), '_blank');
}
async function pollSession() {
  try {
    const r = await fetch('/api/session'); const d = await r.json(); const s = d.session;
    document.getElementById('sessInfo').innerHTML = s.active
      ? `<strong>${s.gameName}</strong> · ${s.durationMin} мин · средний FPS: <strong>${s.avgFps ?? '--'}</strong> · просадок: <strong>${s.dropsCount}</strong>`
      : 'Нет активной игры — запусти игру, запись начнётся автоматически.';
    const rep = document.getElementById('reportsList');
    if (d.reports && d.reports.length > 0)
      rep.innerHTML = '<strong>Сохранённые отчёты:</strong><br>' + d.reports.map(f => `<div style="padding:3px 0"><a href="/reports/${encodeURIComponent(f)}" target="_blank">${f}</a></div>`).join('');
  } catch {}
}
async function poll() { try { const r = await fetch('/api/metrics'); latestData = await r.json(); updateValues(); } catch {} }
init();
</script>
</body>
</html>
""";
}