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
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly CancellationTokenSource _cts = new();
    private string _latestJson = "{}";

    public TelemetryServer(int port, ConfigManager config, SessionAnalyzer sessions, MediaController media)
    {
        _config = config;
        _sessions = sessions;
        _media = media;
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
            catch
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, "{\"ok\":false}");
            }
            return;
        }

        if (path == "/api/session" && req.HttpMethod == "GET")
        {
            await WriteJsonAsync(res, JsonSerializer.Serialize(new
            {
                session = _sessions.GetStatus(),
                reports = _sessions.ListReports()
            }));
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
            res.StatusCode = 404;
            res.Close();
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
<title>PC Monitor USB — Редактор экрана и отчёты</title>
<style>
  * { box-sizing: border-box; }
  body { background:#0b0f14; color:#e6edf3; font-family:system-ui,-apple-system,sans-serif; margin:0; padding:22px; max-width:980px; margin:auto; }
  h1 { font-size:22px; margin:0 0 6px; }
  h2 { font-size:16px; margin:24px 0 10px; color:#c9d1d9; }
  .sub { color:#8b949e; margin-bottom:16px; font-size:13px; }
  .topBar { display:flex; gap:10px; align-items:center; flex-wrap:wrap; margin-bottom:18px; }
  .pill { background:#161b22; border:1px solid #30363d; padding:7px 13px; border-radius:999px; font-size:13px; }
  .ok { color:#3fb950; } .warn { color:#d29922; }
  .btn { background:#21262d; color:#e6edf3; border:1px solid #30363d; border-radius:8px; padding:7px 13px; cursor:pointer; font-size:13px; font-weight:600; }
  .btn:hover { background:#30363d; }
  .btnGreen { background:#238636; border-color:#2ea043; }
  .btnGreen:hover { background:#2ea043; }

  /* Mini-Phone Live Preview */
  .phoneWrap { display:flex; justify-content:center; margin:12px 0 20px; }
  .phoneFrame {
    background:#090c10; border:5px solid #2b3442; border-radius:28px; padding:12px;
    box-shadow:0 16px 40px rgba(0,0,0,0.55); transition:all .2s ease;
    width:680px; min-height:330px; display:flex; flex-direction:column;
  }
  .phoneFrame.portrait { width:340px; min-height:560px; }
  .phoneHeader { display:flex; justify-content:space-between; align-items:center; padding:0 4px 8px; font-size:11px; color:#8b949e; }
  .phoneGrid { flex:1; display:grid; gap:8px; grid-template-columns:repeat(3, 1fr); }
  .phoneFrame.portrait .phoneGrid { grid-template-columns:repeat(2, 1fr); }
  .tile {
    background:#131922; border:1px solid #243042; border-radius:12px; padding:10px 12px;
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
  .tileVal { font-family:ui-monospace,Consolas,monospace; font-size:28px; font-weight:800; margin:6px 0 2px; color:#58a6ff; }
  .tileUnit { font-size:13px; color:#8b949e; margin-left:4px; }
  .tileSub { font-size:10px; color:#6e7681; min-height:13px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .bar { height:4px; background:#21262d; border-radius:3px; margin-top:6px; overflow:hidden; }
  .barFill { height:100%; width:0%; background:#58a6ff; transition:width .25s; }
  .phoneMedia {
    margin-top:8px; background:#121720; border:1px solid #243042; border-radius:10px;
    padding:6px 10px; display:flex; align-items:center; justify-content:space-between; gap:8px; font-size:12px;
  }
  .mediaMeta { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; color:#c9d1d9; }
  .mediaBtns { display:flex; gap:6px; }
  .mBtn { background:#1f2733; border:1px solid #30363d; color:#e6edf3; border-radius:6px; padding:3px 9px; cursor:pointer; font-size:12px; }
  .mBtn:hover { background:#30363d; }

  .chips { display:flex; flex-wrap:wrap; gap:8px; }
  .chip {
    background:#161b22; border:1px solid #30363d; border-radius:10px; padding:8px 12px;
    cursor:pointer; font-size:13px; display:flex; align-items:center; gap:8px; user-select:none;
  }
  .chip.on { border-color:#58a6ff; background:#132033; }
  .card { background:#161b22; border:1px solid #30363d; border-radius:12px; padding:16px; }
  .row { display:flex; align-items:center; justify-content:space-between; gap:12px; flex-wrap:wrap; }
  a { color:#58a6ff; text-decoration:none; }
  a:hover { text-decoration:underline; }
</style>
</head>
<body>
  <h1>PC Monitor USB — живой редактор экрана</h1>
  <div class="sub">Перетаскивай плитки мышкой прямо внутри мини-телефона — порядок и набор виджетов мгновенно обновятся на реальном телефоне.</div>

  <div class="topBar">
    <div class="pill" id="usb">USB: проверка...</div>
    <div class="pill" id="fpsState">PresentMon: проверка...</div>
    <button class="btn" id="orientBtn" onclick="toggleOrient()">⟳ Вид: Горизонтальный</button>
  </div>

  <div class="phoneWrap">
    <div class="phoneFrame" id="phoneFrame">
      <div class="phoneHeader">
        <span id="phStatus" class="ok">● USB подключён</span>
        <span id="phGame"></span>
      </div>
      <div class="phoneGrid" id="phoneGrid"></div>
      <div class="phoneMedia">
        <div class="mediaMeta" id="mediaInfo">♪ Плеер Windows: ничего не играет</div>
        <div class="mediaBtns">
          <button class="mBtn" onclick="mediaCmd('prev')">⏮</button>
          <button class="mBtn" id="playBtn" onclick="mediaCmd('play_pause')">⏯</button>
          <button class="mBtn" onclick="mediaCmd('next')">⏭</button>
        </div>
      </div>
    </div>
  </div>

  <h2>Добавить или убрать виджеты</h2>
  <div class="chips" id="chips"></div>

  <h2>Игровой сеанс и отчёты просадок</h2>
  <div class="card">
    <div class="row">
      <div id="sessInfo">Нет активной игры — запусти игру, запись начнётся автоматически.</div>
      <button class="btn btnGreen" onclick="saveReport()">Сохранить отчёт сейчас</button>
    </div>
    <div id="reportsList" style="padding-top:10px;font-size:13px;color:#8b949e">Сохранённых отчётов пока нет.</div>
  </div>

<script>
let enabled = [];
let allMetrics = [];
let latestData = {};
let portrait = false;
let dragId = null;

async function init() {
  const res = await fetch('/api/config');
  const data = await res.json();
  enabled = data.enabledMetrics;
  allMetrics = data.allMetrics;
  renderAll();
  setInterval(poll, 800);
  setInterval(pollSession, 3000);
  poll();
  pollSession();
}

function toggleOrient() {
  portrait = !portrait;
  document.getElementById('phoneFrame').classList.toggle('portrait', portrait);
  document.getElementById('orientBtn').textContent = portrait ? '⟳ Вид: Вертикальный' : '⟳ Вид: Горизонтальный';
  updateGridCols();
}

function updateGridCols() {
  const grid = document.getElementById('phoneGrid');
  const count = Math.max(1, enabled.length);
  const cols = count <= 2 ? count : (portrait ? 2 : (count <= 6 ? 3 : 4));
  grid.style.gridTemplateColumns = `repeat(${cols}, 1fr)`;
}

function renderAll() {
  renderChips();
  renderPhone();
  updateValues();
}

function renderChips() {
  const box = document.getElementById('chips');
  box.innerHTML = allMetrics.map(m => {
    const on = enabled.includes(m.id);
    return `<div class="chip ${on ? 'on' : ''}" onclick="toggleMetric('${m.id}')">${on ? '✓' : '+'} ${m.label}</div>`;
  }).join('');
}

function renderPhone() {
  const grid = document.getElementById('phoneGrid');
  updateGridCols();
  grid.innerHTML = enabled.map(id => {
    const meta = allMetrics.find(x => x.id === id) || { label: id, unit: '' };
    return `
      <div class="tile" draggable="true" data-id="${id}"
           ondragstart="onDragStart(event,'${id}')"
           ondragover="onDragOver(event)"
           ondragleave="onDragLeave(event)"
           ondrop="onDrop(event,'${id}')"
           ondragend="onDragEnd(event)">
        <div class="tileTop">
          <span class="tileLbl">${meta.label}</span>
          <button class="tileRemove" title="Скрыть" onclick="event.stopPropagation(); toggleMetric('${id}')">×</button>
        </div>
        <div><span class="tileVal" id="pv_${id}">--</span><span class="tileUnit" id="pu_${id}">${meta.unit}</span></div>
        <div class="tileSub" id="ps_${id}"></div>
        <div class="bar"><div class="barFill" id="pb_${id}"></div></div>
      </div>`;
  }).join('');
}

function onDragStart(e, id) {
  dragId = id;
  e.currentTarget.classList.add('dragging');
  e.dataTransfer.effectAllowed = 'move';
}
function onDragOver(e) {
  e.preventDefault();
  e.currentTarget.classList.add('over');
}
function onDragLeave(e) {
  e.currentTarget.classList.remove('over');
}
function onDragEnd(e) {
  e.currentTarget.classList.remove('dragging');
  document.querySelectorAll('.tile.over').forEach(el => el.classList.remove('over'));
}
async function onDrop(e, targetId) {
  e.preventDefault();
  e.currentTarget.classList.remove('over');
  if (!dragId || dragId === targetId) return;
  const from = enabled.indexOf(dragId);
  const to = enabled.indexOf(targetId);
  if (from < 0 || to < 0) return;
  enabled.splice(from, 1);
  enabled.splice(to, 0, dragId);
  dragId = null;
  renderAll();
  await saveConfig();
}

async function toggleMetric(id) {
  if (enabled.includes(id)) {
    if (enabled.length <= 1) return;
    enabled = enabled.filter(x => x !== id);
  } else {
    enabled.push(id);
  }
  renderAll();
  await saveConfig();
}

async function saveConfig() {
  await fetch('/api/config', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabledMetrics: enabled })
  });
}

async function mediaCmd(action) {
  await fetch('/api/media', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action })
  });
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

function colorForLoad(v) { return v >= 90 ? '#f85149' : v >= 75 ? '#d29922' : '#58a6ff'; }
function colorForTemp(v) { return v >= 85 ? '#f85149' : v >= 72 ? '#d29922' : '#3fb950'; }

function setTile(id, val, sub, pct, color) {
  const vEl = document.getElementById('pv_' + id);
  const sEl = document.getElementById('ps_' + id);
  const bEl = document.getElementById('pb_' + id);
  if (!vEl) return;
  vEl.textContent = val;
  vEl.style.color = color;
  if (sEl) sEl.textContent = sub || '';
  if (bEl) { bEl.style.width = Math.max(0, Math.min(100, pct || 0)) + '%'; bEl.style.background = color; }
}

function updateValues() {
  const d = latestData;
  if (!d || Object.keys(d).length === 0) return;

  document.getElementById('usb').innerHTML = d.usbConnected
    ? '<span class="ok">● USB подключён (' + (d.deviceSerial || 'Android') + ')</span>'
    : '<span class="warn">○ Телефон не найден по USB</span>';
  document.getElementById('phStatus').innerHTML = d.usbConnected
    ? '<span class="ok">● USB подключён</span>' : '<span class="warn">○ Ожидание USB</span>';
  document.getElementById('fpsState').innerHTML = d.presentMonActive
    ? '<span class="ok">● PresentMon активен</span>' : '<span class="warn">○ PresentMon не запущен</span>';
  document.getElementById('phGame').textContent = d.activeGame ? ('Игра: ' + d.activeGame) : '';

  if (d.mediaTitle) {
    document.getElementById('mediaInfo').textContent = (d.mediaPlaying ? '▶ ' : '⏸ ') + d.mediaTitle + (d.mediaArtist ? ' — ' + d.mediaArtist : '');
    document.getElementById('playBtn').textContent = d.mediaPlaying ? '⏸' : '▶';
  } else {
    document.getElementById('mediaInfo').textContent = '♪ Плеер Windows: ничего не играет';
    document.getElementById('playBtn').textContent = '⏯';
  }

  setTile('fps', d.fps ?? '--', d.fps1Low ? ('1% low: ' + d.fps1Low) : (d.activeGame || ''), d.fps ? (d.fps * 100 / 165) : 0, d.fps == null ? '#8b949e' : d.fps >= 60 ? '#3fb950' : d.fps >= 30 ? '#d29922' : '#f85149');
  setTile('cpu_load', d.cpuLoad != null ? Math.round(d.cpuLoad) : '--', d.cpuName || '', d.cpuLoad, d.cpuLoad != null ? colorForLoad(d.cpuLoad) : '#8b949e');
  setTile('cpu_temp', d.cpuTemp != null ? Math.round(d.cpuTemp) : '--', 'Tctl / Package', d.cpuTemp, d.cpuTemp != null ? colorForTemp(d.cpuTemp) : '#8b949e');
  setTile('gpu_load', d.gpuLoad != null ? Math.round(d.gpuLoad) : '--', d.gpuName || '', d.gpuLoad, d.gpuLoad != null ? colorForLoad(d.gpuLoad) : '#8b949e');
  setTile('gpu_temp', d.gpuTemp != null ? Math.round(d.gpuTemp) : '--', 'GPU Core', d.gpuTemp, d.gpuTemp != null ? colorForTemp(d.gpuTemp) : '#8b949e');
  setTile('ram_load', d.ramLoad != null ? Math.round(d.ramLoad) : '--', d.ramUsedGb != null ? (d.ramUsedGb + ' / ' + (d.ramTotalGb ?? '?') + ' ГБ') : '', d.ramLoad, d.ramLoad != null ? colorForLoad(d.ramLoad) : '#8b949e');
  const vPct = (d.gpuVramUsedGb != null && d.gpuVramTotalGb) ? (d.gpuVramUsedGb / d.gpuVramTotalGb * 100) : 0;
  setTile('gpu_vram', d.gpuVramUsedGb != null ? d.gpuVramUsedGb.toFixed(1) : '--', d.gpuVramTotalGb ? ('из ' + d.gpuVramTotalGb.toFixed(1) + ' ГБ') : '', vPct, d.gpuVramUsedGb != null ? colorForLoad(vPct) : '#8b949e');
  setTile('cpu_power', d.cpuPower != null ? Math.round(d.cpuPower) : '--', '', d.cpuPower ? (d.cpuPower / 120 * 100) : 0, '#58a6ff');
  setTile('gpu_power', d.gpuPower != null ? Math.round(d.gpuPower) : '--', '', d.gpuPower ? (d.gpuPower / 200 * 100) : 0, '#a371f7');
  setTile('cpu_clock', d.cpuClock != null ? Math.round(d.cpuClock) : '--', '', d.cpuClock ? (d.cpuClock / 5000 * 100) : 0, '#58a6ff');
  setTile('gpu_clock', d.gpuClock != null ? Math.round(d.gpuClock) : '--', '', d.gpuClock ? (d.gpuClock / 3000 * 100) : 0, '#a371f7');
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
