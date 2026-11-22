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
                enabledMetrics = cfg.EnabledMetrics,
                theme = cfg.Theme,
                fontScale = cfg.FontScale,
                standbyMode = cfg.StandbyMode,
                obsPort = cfg.ObsPort,
                obsPassword = cfg.ObsPassword,
                macros = cfg.Macros,
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
                var root = doc.RootElement;

                List<string>? metrics = null;
                if (root.TryGetProperty("enabledMetrics", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    metrics = arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();

                string? theme = root.TryGetProperty("theme", out var th) ? th.GetString() : null;
                double? fontScale = root.TryGetProperty("fontScale", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetDouble() : null;
                string? standby = root.TryGetProperty("standbyMode", out var sb) ? sb.GetString() : null;
                int? obsPort = root.TryGetProperty("obsPort", out var op) && op.ValueKind == JsonValueKind.Number ? op.GetInt32() : null;
                string? obsPass = root.TryGetProperty("obsPassword", out var opw) ? opw.GetString() : null;

                List<MacroButton>? macros = null;
                if (root.TryGetProperty("macros", out var mc) && mc.ValueKind == JsonValueKind.Array)
                    macros = JsonSerializer.Deserialize<List<MacroButton>>(mc.GetRawText());

                _config.UpdateConfig(metrics, theme, fontScale, macros, standby, obsPort, obsPass);
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
<title>PC Monitor USB — Студия экрана v8</title>
<style>
  * { box-sizing: border-box; }
  body { background:#0b0f14; color:#e6edf3; font-family:system-ui,-apple-system,sans-serif; margin:0; padding:22px; max-width:980px; margin:auto; }
  h1 { font-size:22px; margin:0 0 6px; }
  h2 { font-size:15px; margin:20px 0 10px; color:#c9d1d9; }
  .sub { color:#8b949e; margin-bottom:16px; font-size:13px; }
  .topBar { display:flex; gap:9px; align-items:center; flex-wrap:wrap; margin-bottom:16px; }
  .pill { background:#161b22; border:1px solid #30363d; padding:7px 13px; border-radius:999px; font-size:13px; }
  .ok { color:#3fb950; } .warn { color:#d29922; }
  .btn { background:#21262d; color:#e6edf3; border:1px solid #30363d; border-radius:8px; padding:7px 13px; cursor:pointer; font-size:13px; font-weight:600; }
  .btn:hover { background:#30363d; }
  .btnGreen { background:#238636; border-color:#2ea043; }
  .btnGreen:hover { background:#2ea043; }
  .btnOn { border-color:#58a6ff; background:#132033; color:#58a6ff; }

  .phoneWrap { display:flex; justify-content:center; margin:12px 0 18px; }
  .phoneFrame {
    background:#090c10; background-size:cover; background-position:center;
    border:5px solid #2b3442; border-radius:28px; padding:12px;
    box-shadow:0 16px 40px rgba(0,0,0,0.55); transition:all .2s ease;
    width:700px; min-height:345px; display:flex; flex-direction:column;
  }
  .phoneFrame.portrait { width:345px; min-height:580px; }
  .phoneHeader { display:flex; justify-content:space-between; align-items:center; padding:0 4px 8px; font-size:11px; color:#8b949e; }
  .phoneGrid { flex:1; display:grid; gap:8px; grid-template-columns:repeat(3, 1fr); }
  .tile {
    background:rgba(19,25,34,0.88); border:1px solid #243042; border-radius:12px; padding:10px 12px;
    display:flex; flex-direction:column; justify-content:space-between;
    cursor:grab; user-select:none; position:relative; transition:transform .12s, border-color .12s;
  }
  .tile:active { cursor:grabbing; }
  .tile.dragging { opacity:0.4; transform:scale(0.96); }
  .tile.over { border-color:#58a6ff; box-shadow:0 0 0 2px rgba(88,166,255,0.28); }
  .tileTop { display:flex; justify-content:space-between; align-items:center; }
  .tileLbl { font-size:10px; font-weight:700; text-transform:uppercase; color:#8b949e; letter-spacing:0.4px; }
  .tileRemove { background:none; border:none; color:#6e7681; cursor:pointer; font-size:14px; line-height:1; padding:0 2px; }
  .tileRemove:hover { color:#f85149; }
  .tileVal { font-family:ui-monospace,Consolas,monospace; font-size:28px; font-weight:800; margin:4px 0 2px; color:#58a6ff; }
  .tileUnit { font-size:13px; color:#8b949e; margin-left:4px; }
  .tileSub { font-size:10px; color:#8b949e; min-height:13px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .spark { width:100%; height:22px; margin-top:4px; display:block; }
  .phoneMedia {
    margin-top:8px; background:rgba(18,23,32,0.88); border:1px solid #243042; border-radius:10px;
    padding:6px 10px; display:flex; align-items:center; justify-content:space-between; gap:10px; font-size:12px;
  }
  .artImg { width:28px; height:28px; border-radius:6px; object-fit:cover; display:none; }
  .mediaMeta { flex:1; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; color:#c9d1d9; }
  .mediaBtns { display:flex; gap:6px; }
  .mBtn { background:#1f2733; border:1px solid #30363d; color:#e6edf3; border-radius:6px; padding:3px 9px; cursor:pointer; font-size:12px; }
  .mBtn:hover { background:#30363d; }

  .controlsRow { display:flex; gap:10px; flex-wrap:wrap; align-items:center; background:#161b22; border:1px solid #30363d; border-radius:12px; padding:12px 16px; margin-bottom:12px; }
  .chips { display:flex; flex-wrap:wrap; gap:8px; }
  .chip { background:#161b22; border:1px solid #30363d; border-radius:10px; padding:7px 12px; cursor:pointer; font-size:13px; user-select:none; }
  .chip.on { border-color:#58a6ff; background:#132033; }
  .card { background:#161b22; border:1px solid #30363d; border-radius:12px; padding:16px; }
  .row { display:flex; align-items:center; justify-content:space-between; gap:12px; flex-wrap:wrap; }
  .macroGrid { display:grid; grid-template-columns:repeat(auto-fit,minmax(260px,1fr)); gap:10px; }
  .macroRow { display:flex; gap:6px; align-items:center; }
  input[type=text], input[type=password], input[type=number] { background:#0d1117; border:1px solid #30363d; color:#e6edf3; border-radius:6px; padding:6px 8px; font-size:12px; width:100%; }
  a { color:#58a6ff; text-decoration:none; }
  a:hover { text-decoration:underline; }
</style>
</head>
<body>
  <h1>PC Monitor USB — студия экрана v8</h1>
  <div class="sub">Перетаскивай плитки, загружай свой фон, подключай OBS WebSocket и обновляй APK на телефоне в 1 клик.</div>

  <div class="topBar">
    <div class="pill" id="usb">USB: проверка...</div>
    <div class="pill" id="fpsState">PresentMon: проверка...</div>
    <div class="pill" id="obsState">OBS: не подключён</div>
    <button class="btn" id="orientBtn" onclick="toggleOrient()">⟳ Вид: Горизонтальный</button>
    <button class="btn" id="autoBtn" onclick="toggleAutoStart()">Автозапуск: ...</button>
    <button class="btn btnGreen" id="apkBtn" onclick="updatePhoneApk()">📲 Обновить APK по USB</button>
  </div>

  <div class="phoneWrap">
    <div class="phoneFrame" id="phoneFrame">
      <div class="phoneHeader">
        <span id="phStatus" class="ok">● USB подключён</span>
        <span id="phGame"></span>
      </div>
      <div class="phoneGrid" id="phoneGrid"></div>
      <div class="phoneMedia">
        <img id="artImg" class="artImg" alt="">
        <div class="mediaMeta" id="mediaInfo">♪ Плеер Windows: ничего не играет</div>
        <div class="mediaBtns">
          <button class="mBtn" onclick="mediaCmd('prev')">⏮</button>
          <button class="mBtn" id="playBtn" onclick="mediaCmd('play_pause')">⏯</button>
          <button class="mBtn" onclick="mediaCmd('next')">⏭</button>
        </div>
      </div>
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

  <div class="controlsRow">
    <strong>Фон экрана:</strong>
    <input type="file" id="bgFile" accept="image/*" style="display:none" onchange="uploadBg(this)">
    <button class="btn" onclick="document.getElementById('bgFile').click()">🖼 Загрузить картинку</button>
    <button class="btn" onclick="clearBg()">Убрать фон</button>
    <div style="margin-left:auto;display:flex;align-items:center;gap:8px">
      <strong>Настольные часы вне игры:</strong>
      <button class="btn" id="standbyBtn" onclick="toggleStandby()">Выкл</button>
    </div>
  </div>

  <h2>Виджеты на экране</h2>
  <div class="chips" id="chips"></div>

  <h2>OBS Studio WebSocket (живые кнопки стрима и сцен на телефоне)</h2>
  <div class="card">
    <div class="row">
      <div style="display:flex;gap:8px;align-items:center;flex:1;min-width:260px">
        <span style="font-size:12px;color:#8b949e;white-space:nowrap">Порт OBS:</span>
        <input type="number" id="obsPort" value="4455" style="width:90px">
        <span style="font-size:12px;color:#8b949e;white-space:nowrap">Пароль (если есть):</span>
        <input type="password" id="obsPass" placeholder="Пусто, если без пароля">
      </div>
      <button class="btn btnGreen" onclick="saveObs()">Сохранить OBS</button>
    </div>
    <div style="font-size:12px;color:#8b949e;margin-top:8px">В OBS открой: Инструменты → Настройки сервера WebSocket → Включить сервер WebSocket.</div>
  </div>

  <h2>Стримерские кнопки (свайп влево на телефоне)</h2>
  <div class="card">
    <div class="macroGrid" id="macroList"></div>
    <div style="margin-top:12px;display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px">
      <span style="font-size:12px;color:#8b949e">Горячие клавиши вида: Ctrl+Shift+M, Alt+F9, Win+PrintScreen, F13, vol_mute</span>
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
const THEMES = {
  midnight: { bg:'#090c10', card:'rgba(19,25,34,0.88)', border:'#243042', accent:'#58a6ff' },
  amoled:   { bg:'#000000', card:'rgba(10,10,10,0.90)', border:'#1f242c', accent:'#00e676' },
  spide:    { bg:'#14100e', card:'rgba(33,26,22,0.88)', border:'#3a2d26', accent:'#ff8a50' },
  cyber:    { bg:'#0b0814', card:'rgba(22,16,38,0.88)', border:'#352459', accent:'#d946ef' }
};

let enabled = [], allMetrics = [], macros = [], latestData = {};
let theme = 'midnight', fontScale = 1.0, standbyMode = 'off', portrait = false, dragId = null, lastArtVer = -1, lastBgVer = -1, autoStartOn = false;
const history = {};

async function init() {
  const res = await fetch('/api/config');
  const data = await res.json();
  enabled = data.enabledMetrics || [];
  allMetrics = data.allMetrics || [];
  theme = data.theme || 'midnight';
  fontScale = data.fontScale || 1.0;
  standbyMode = data.standbyMode || 'off';
  macros = data.macros || [];

  document.getElementById('fontSlider').value = fontScale;
  document.getElementById('fontVal').textContent = Math.round(fontScale * 100) + '%';
  document.getElementById('obsPort').value = data.obsPort || 4455;
  document.getElementById('obsPass').value = data.obsPassword || '';
  updateStandbyBtn();

  renderAll(); renderMacros(); pollAutoStart();
  setInterval(poll, 800); setInterval(pollSession, 3000);
  poll(); pollSession();
}

function updateStandbyBtn() {
  const b = document.getElementById('standbyBtn');
  b.textContent = standbyMode === 'auto' ? '✓ Авто-часы (30 сек)' : 'Выкл';
  b.classList.toggle('btnOn', standbyMode === 'auto');
}

async function toggleStandby() {
  standbyMode = standbyMode === 'auto' ? 'off' : 'auto';
  updateStandbyBtn();
  await saveConfig();
}

async function uploadBg(input) {
  if (!input.files || !input.files[0]) return;
  await fetch('/api/wallpaper', { method:'POST', body: input.files[0] });
  input.value = '';
  setTimeout(poll, 250);
}

async function clearBg() {
  await fetch('/api/wallpaper', { method:'DELETE' });
  setTimeout(poll, 250);
}

async function saveObs() {
  await saveConfig();
  alert('Настройки OBS сохранены — агент переподключится через пару секунд.');
}

async function updatePhoneApk() {
  const b = document.getElementById('apkBtn');
  b.disabled = true; b.textContent = '⏳ Устанавливаю...';
  try {
    const r = await fetch('/api/update-apk', { method: 'POST' });
    const d = await r.json();
    b.textContent = d.ok ? '✓ Обновлено!' : ('✕ ' + (d.message || 'Ошибка'));
  } catch { b.textContent = '✕ Ошибка связи'; }
  setTimeout(() => { b.disabled = false; b.textContent = '📲 Обновить APK по USB'; }, 3500);
}

async function pollAutoStart() {
  try {
    const r = await fetch('/api/autostart');
    const d = await r.json();
    autoStartOn = !!d.enabled;
    const b = document.getElementById('autoBtn');
    b.textContent = autoStartOn ? '✓ Автозапуск: Вкл' : 'Автозапуск: Выкл';
    b.classList.toggle('btnOn', autoStartOn);
  } catch {}
}

async function toggleAutoStart() {
  const r = await fetch('/api/autostart', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ enabled: !autoStartOn }) });
  const d = await r.json();
  autoStartOn = !!d.enabled;
  const b = document.getElementById('autoBtn');
  b.textContent = autoStartOn ? '✓ Автозапуск: Вкл' : 'Автозапуск: Выкл';
  b.classList.toggle('btnOn', autoStartOn);
}

function toggleOrient() {
  portrait = !portrait;
  document.getElementById('phoneFrame').classList.toggle('portrait', portrait);
  document.getElementById('orientBtn').textContent = portrait ? '⟳ Вид: Вертикальный' : '⟳ Вид: Горизонтальный';
  updateGridCols();
}

function setTheme(t) { theme = t; applyThemeUI(); saveConfig(); }

function setFontScale(v) {
  fontScale = parseFloat(v);
  document.getElementById('fontVal').textContent = Math.round(fontScale * 100) + '%';
  document.querySelectorAll('.tileVal').forEach(el => el.style.fontSize = Math.round(28 * fontScale) + 'px');
  saveConfig();
}

function applyThemeUI() {
  const th = THEMES[theme] || THEMES.midnight;
  const pf = document.getElementById('phoneFrame');
  if (!pf.style.backgroundImage) pf.style.backgroundColor = th.bg;
  pf.style.borderColor = th.border;
  document.querySelectorAll('.tile').forEach(el => { el.style.background = th.card; el.style.borderColor = th.border; });
  for (const k of Object.keys(THEMES)) {
    const b = document.getElementById('th_' + k);
    if (b) b.classList.toggle('btnOn', k === theme);
  }
}

function updateGridCols() {
  const grid = document.getElementById('phoneGrid');
  const count = Math.max(1, enabled.length);
  const cols = count <= 2 ? count : (portrait ? 2 : (count <= 6 ? 3 : 4));
  grid.style.gridTemplateColumns = `repeat(${cols}, 1fr)`;
}

function renderAll() { renderChips(); renderPhone(); applyThemeUI(); updateValues(); }

function renderChips() {
  document.getElementById('chips').innerHTML = allMetrics.map(m => {
    const on = enabled.includes(m.id);
    return `<div class="chip ${on ? 'on' : ''}" onclick="toggleMetric('${m.id}')">${on ? '✓' : '+'} ${m.label}</div>`;
  }).join('');
}

function renderPhone() {
  const grid = document.getElementById('phoneGrid');
  updateGridCols();
  const px = Math.round(28 * fontScale);
  grid.innerHTML = enabled.map(id => {
    const meta = allMetrics.find(x => x.id === id) || { label: id, unit: '' };
    return `
      <div class="tile" draggable="true" data-id="${id}"
           ondragstart="onDragStart(event,'${id}')" ondragover="onDragOver(event)"
           ondragleave="onDragLeave(event)" ondrop="onDrop(event,'${id}')" ondragend="onDragEnd(event)">
        <div class="tileTop">
          <span class="tileLbl">${meta.label}</span>
          <button class="tileRemove" title="Скрыть" onclick="event.stopPropagation(); toggleMetric('${id}')">×</button>
        </div>
        <div><span class="tileVal" id="pv_${id}" style="font-size:${px}px">--</span><span class="tileUnit">${meta.unit}</span></div>
        <div class="tileSub" id="ps_${id}"></div>
        <svg class="spark" id="sp_${id}" viewBox="0 0 100 24" preserveAspectRatio="none"></svg>
      </div>`;
  }).join('');
}

function renderMacros() {
  document.getElementById('macroList').innerHTML = macros.map((m, i) => `
    <div class="macroRow">
      <input type="text" id="ml_${i}" value="${m.label}" placeholder="Название">
      <input type="text" id="mh_${i}" value="${m.hotkey}" placeholder="Клавиши">
      <button class="mBtn" title="Тест" onclick="testMacro(${i})">▶</button>
    </div>
  `).join('');
}

async function saveMacros() {
  macros = macros.map((m, i) => ({
    id: m.id,
    label: document.getElementById('ml_' + i).value.trim() || m.label,
    hotkey: document.getElementById('mh_' + i).value.trim() || m.hotkey
  }));
  await saveConfig();
}

async function testMacro(i) {
  const hk = document.getElementById('mh_' + i).value.trim();
  await fetch('/api/macro', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ hotkey: hk }) });
}

function onDragStart(e, id) { dragId = id; e.currentTarget.classList.add('dragging'); e.dataTransfer.effectAllowed = 'move'; }
function onDragOver(e) { e.preventDefault(); e.currentTarget.classList.add('over'); }
function onDragLeave(e) { e.currentTarget.classList.remove('over'); }
function onDragEnd(e) { e.currentTarget.classList.remove('dragging'); document.querySelectorAll('.tile.over').forEach(el => el.classList.remove('over')); }
async function onDrop(e, targetId) {
  e.preventDefault(); e.currentTarget.classList.remove('over');
  if (!dragId || dragId === targetId) return;
  const from = enabled.indexOf(dragId), to = enabled.indexOf(targetId);
  if (from < 0 || to < 0) return;
  enabled.splice(from, 1); enabled.splice(to, 0, dragId); dragId = null;
  renderAll(); await saveConfig();
}

async function toggleMetric(id) {
  if (enabled.includes(id)) { if (enabled.length <= 1) return; enabled = enabled.filter(x => x !== id); }
  else enabled.push(id);
  renderAll(); await saveConfig();
}

async function saveConfig() {
  await fetch('/api/config', {
    method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({
      enabledMetrics: enabled, theme, fontScale, standbyMode, macros,
      obsPort: parseInt(document.getElementById('obsPort').value || '4455', 10),
      obsPassword: document.getElementById('obsPass').value || ''
    })
  });
}

async function mediaCmd(action) {
  await fetch('/api/media', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ action }) });
  setTimeout(poll, 250);
}

async function saveReport() {
  const r = await fetch('/api/session/save', { method: 'POST' });
  const d = await r.json();
  await pollSession();
  if (d.ok && d.file) window.open('/reports/' + encodeURIComponent(d.file), '_blank');
}

async function pollSession() {
  try {
    const r = await fetch('/api/session');
    const d = await r.json();
    const s = d.session;
    document.getElementById('sessInfo').innerHTML = s.active
      ? `<strong>${s.gameName}</strong> · ${s.durationMin} мин · средний FPS: <strong>${s.avgFps ?? '--'}</strong> · просадок: <strong>${s.dropsCount}</strong>`
      : 'Нет активной игры — запусти игру, запись начнётся автоматически (альт-табы не сбрасывают сеанс).';
    const rep = document.getElementById('reportsList');
    if (d.reports && d.reports.length > 0) {
      rep.innerHTML = '<strong>Сохранённые отчёты (папка reports\\):</strong><br>' +
        d.reports.map(f => `<div style="padding:4px 0"><a href="/reports/${encodeURIComponent(f)}" target="_blank">${f}</a></div>`).join('');
    }
  } catch {}
}

function colorForLoad(v) { return v >= 90 ? '#f85149' : v >= 75 ? '#d29922' : (THEMES[theme]?.accent || '#58a6ff'); }
function colorForTemp(v) { return v >= 85 ? '#f85149' : v >= 72 ? '#d29922' : '#3fb950'; }

function pushSpark(id, num, maxVal, color) {
  if (num == null || isNaN(num)) return;
  if (!history[id]) history[id] = [];
  history[id].push(Math.max(0, Math.min(1, num / maxVal)));
  if (history[id].length > 35) history[id].shift();
  const svg = document.getElementById('sp_' + id);
  if (!svg || history[id].length < 2) return;
  const pts = history[id].map((v, i) => `${(i / (history[id].length - 1) * 100).toFixed(1)},${(22 - v * 20).toFixed(1)}`).join(' ');
  svg.innerHTML = `<polyline fill="none" stroke="${color}" stroke-width="1.8" points="${pts}" />`;
}

function setTile(id, val, sub, numForSpark, maxVal, color) {
  const vEl = document.getElementById('pv_' + id);
  const sEl = document.getElementById('ps_' + id);
  if (!vEl) return;
  vEl.textContent = val; vEl.style.color = color;
  if (sEl) sEl.textContent = sub || '';
  pushSpark(id, numForSpark, maxVal, color);
}

function updateValues() {
  const d = latestData;
  if (!d || Object.keys(d).length === 0) return;

  document.getElementById('usb').innerHTML = d.usbConnected ? '<span class="ok">● USB подключён (' + (d.deviceSerial || 'Android') + ')</span>' : '<span class="warn">○ Телефон не найден по USB</span>';
  document.getElementById('phStatus').innerHTML = d.usbConnected ? '<span class="ok">● USB подключён</span>' : '<span class="warn">○ Ожидание USB</span>';
  document.getElementById('fpsState').innerHTML = d.presentMonActive ? '<span class="ok">● PresentMon активен</span>' : '<span class="warn">○ PresentMon не запущен</span>';
  document.getElementById('obsState').innerHTML = d.obsConnected
    ? '<span class="ok">● OBS подключён' + (d.obsStreaming ? ' (LIVE ' + (d.obsStreamTime || '') + ')' : '') + '</span>'
    : '<span class="warn">○ OBS не подключён</span>';
  document.getElementById('phGame').textContent = d.activeGame ? ('Игра: ' + d.activeGame) : '';

  if (d.wallpaperVersion !== lastBgVer) {
    lastBgVer = d.wallpaperVersion;
    const pf = document.getElementById('phoneFrame');
    pf.style.backgroundImage = lastBgVer > 0 ? `linear-gradient(rgba(9,12,16,0.52),rgba(9,12,16,0.72)), url('/api/wallpaper?v=${lastBgVer}')` : '';
  }

  if (d.mediaTitle) {
    document.getElementById('mediaInfo').textContent = (d.mediaPlaying ? '▶ ' : '⏸ ') + d.mediaTitle + (d.mediaArtist ? ' — ' + d.mediaArtist : '');
    document.getElementById('playBtn').textContent = d.mediaPlaying ? '⏸' : '▶';
    if (d.mediaArtVersion !== lastArtVer) {
      lastArtVer = d.mediaArtVersion;
      const img = document.getElementById('artImg');
      img.onload = () => img.style.display = 'block';
      img.onerror = () => img.style.display = 'none';
      img.src = '/api/media/art?v=' + lastArtVer;
    }
  } else {
    document.getElementById('mediaInfo').textContent = '♪ Плеер Windows: ничего не играет';
    document.getElementById('playBtn').textContent = '⏯';
    document.getElementById('artImg').style.display = 'none';
  }

  const acc = THEMES[theme]?.accent || '#58a6ff';
  setTile('fps', d.fps ?? '--', d.fps1Low ? ('1% low: ' + d.fps1Low) : (d.activeGame || ''), d.fps, 165, d.fps == null ? '#8b949e' : d.fps >= 60 ? '#3fb950' : d.fps >= 30 ? '#d29922' : '#f85149');
  setTile('cpu_load', d.cpuLoad != null ? Math.round(d.cpuLoad) : '--', d.cpuName || '', d.cpuLoad, 100, d.cpuLoad != null ? colorForLoad(d.cpuLoad) : '#8b949e');
  setTile('cpu_temp', d.cpuTemp != null ? Math.round(d.cpuTemp) : '--', 'Tctl / Package', d.cpuTemp, 100, d.cpuTemp != null ? colorForTemp(d.cpuTemp) : '#8b949e');
  setTile('gpu_load', d.gpuLoad != null ? Math.round(d.gpuLoad) : '--', d.gpuName || '', d.gpuLoad, 100, d.gpuLoad != null ? colorForLoad(d.gpuLoad) : '#8b949e');
  setTile('gpu_temp', d.gpuTemp != null ? Math.round(d.gpuTemp) : '--', 'GPU Core', d.gpuTemp, 100, d.gpuTemp != null ? colorForTemp(d.gpuTemp) : '#8b949e');
  setTile('ram_load', d.ramLoad != null ? Math.round(d.ramLoad) : '--', d.ramUsedGb != null ? (d.ramUsedGb + ' / ' + (d.ramTotalGb ?? '?') + ' ГБ') : '', d.ramLoad, 100, d.ramLoad != null ? colorForLoad(d.ramLoad) : '#8b949e');
  const vPct = (d.gpuVramUsedGb != null && d.gpuVramTotalGb) ? (d.gpuVramUsedGb / d.gpuVramTotalGb * 100) : null;
  setTile('gpu_vram', d.gpuVramUsedGb != null ? d.gpuVramUsedGb.toFixed(1) : '--', d.gpuVramTotalGb ? ('из ' + d.gpuVramTotalGb.toFixed(1) + ' ГБ') : '', vPct, 100, vPct != null ? colorForLoad(vPct) : '#8b949e');
  setTile('net_speed', d.netUpMbps != null ? ('↑' + d.netUpMbps.toFixed(1)) : '--', d.netDownMbps != null ? ('↓ ' + d.netDownMbps.toFixed(1) + ' Мбит/с') : '', d.netUpMbps, 50, '#2dd4bf');
  setTile('cpu_power', d.cpuPower != null ? Math.round(d.cpuPower) : '--', '', d.cpuPower, 120, acc);
  setTile('gpu_power', d.gpuPower != null ? Math.round(d.gpuPower) : '--', '', d.gpuPower, 200, acc);
  setTile('cpu_clock', d.cpuClock != null ? Math.round(d.cpuClock) : '--', '', d.cpuClock, 5000, acc);
  setTile('gpu_clock', d.gpuClock != null ? Math.round(d.gpuClock) : '--', '', d.gpuClock, 3000, acc);
}

async function poll() {
  try {
    const r = await fetch('/api/metrics');
    latestData = await r.json();
    updateValues();
  } catch {}
}
init();
</script>
</body>
</html>
""";
}
