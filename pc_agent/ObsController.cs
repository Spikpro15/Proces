using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCMonitorAgent;

/// <summary>
/// Lightweight native OBS WebSocket v5 client (OBS Studio 28+).
/// Connects in background, polls stream/record/scene status, and executes live commands.
/// </summary>
public sealed class ObsController : IDisposable
{
    private readonly ConfigManager _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;

    public bool IsConnected { get; private set; }
    public bool IsStreaming { get; private set; }
    public bool IsRecording { get; private set; }
    public string? StreamTimecode { get; private set; }
    public string? CurrentScene { get; private set; }
    public List<string> Scenes { get; private set; } = new();

    public ObsController(ConfigManager config)
    {
        _config = config;
    }

    public void Start() => Task.Run(() => ConnectionLoop(_cts.Token));

    private async Task ConnectionLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = _config.Current;
            try
            {
                using var ws = new ClientWebSocket();
                _ws = ws;
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(3000);
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{cfg.ObsPort}"), connectCts.Token);

                var helloJson = await ReceiveTextAsync(ws, ct);
                if (helloJson is null) continue;

                string? auth = null;
                using (var doc = JsonDocument.Parse(helloJson))
                {
                    if (doc.RootElement.TryGetProperty("d", out var d) &&
                        d.TryGetProperty("authentication", out var authObj))
                    {
                        var salt = authObj.GetProperty("salt").GetString() ?? "";
                        var challenge = authObj.GetProperty("challenge").GetString() ?? "";
                        auth = ComputeObsAuth(cfg.ObsPassword ?? "", salt, challenge);
                    }
                }

                var identifyObj = auth is not null
                    ? (object)new { op = 1, d = new { rpcVersion = 1, authentication = auth } }
                    : new { op = 1, d = new { rpcVersion = 1 } };
                await SendRawAsync(ws, JsonSerializer.Serialize(identifyObj), ct);

                var identified = await ReceiveTextAsync(ws, ct);
                if (identified is null || !identified.Contains("\"op\":2"))
                    continue;

                IsConnected = true;

                // Background poll loop while connected
                var pollTask = Task.Run(async () =>
                {
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        await SendRequestAsync("GetStreamStatus", "poll_stream");
                        await SendRequestAsync("GetRecordStatus", "poll_record");
                        await SendRequestAsync("GetSceneList", "poll_scenes");
                        try { await Task.Delay(1500, ct); } catch { break; }
                    }
                }, ct);

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var msg = await ReceiveTextAsync(ws, ct);
                    if (msg is null) break;
                    HandleMessage(msg);
                }
            }
            catch { }
            finally
            {
                IsConnected = false;
                IsStreaming = false;
                IsRecording = false;
                StreamTimecode = null;
                _ws = null;
            }

            try { await Task.Delay(4000, ct); } catch { break; }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("op", out var opEl) || opEl.GetInt32() != 7) return;
            var d = doc.RootElement.GetProperty("d");
            var reqId = d.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
            if (!d.TryGetProperty("responseData", out var data)) return;

            if (reqId == "poll_stream")
            {
                IsStreaming = data.TryGetProperty("outputActive", out var a) && a.GetBoolean();
                var tc = data.TryGetProperty("outputTimecode", out var t) ? t.GetString() : null;
                StreamTimecode = (IsStreaming && !string.IsNullOrEmpty(tc) && tc.Length >= 8) ? tc[..8] : null;
            }
            else if (reqId == "poll_record")
            {
                IsRecording = data.TryGetProperty("outputActive", out var a) && a.GetBoolean();
            }
            else if (reqId == "poll_scenes")
            {
                CurrentScene = data.TryGetProperty("currentProgramSceneName", out var cs) ? cs.GetString() : null;
                if (data.TryGetProperty("scenes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var s in arr.EnumerateArray())
                        if (s.TryGetProperty("sceneName", out var sn) && sn.GetString() is string name)
                            list.Add(name);
                    list.Reverse(); // OBS returns scenes bottom-to-top
                    Scenes = list.Take(6).ToList();
                }
            }
        }
        catch { }
    }

    public async Task<bool> ExecuteAsync(string action, string? sceneName = null)
    {
        if (!IsConnected || _ws is null || _ws.State != WebSocketState.Open) return false;
        try
        {
            return action switch
            {
                "toggle_stream" => await SendRequestAsync("ToggleStream", "cmd_stream"),
                "toggle_record" => await SendRequestAsync("ToggleRecord", "cmd_rec"),
                "switch_scene" when !string.IsNullOrEmpty(sceneName) =>
                    await SendRequestAsync("SetCurrentProgramScene", "cmd_scene", new { sceneName }),
                _ => false
            };
        }
        catch { return false; }
    }

    private async Task<bool> SendRequestAsync(string requestType, string requestId, object? requestData = null)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return false;
        object payload = requestData is null
            ? new { op = 6, d = new { requestType, requestId } }
            : new { op = 6, d = new { requestType, requestId, requestData } };
        return await SendRawAsync(ws, JsonSerializer.Serialize(payload), _cts.Token);
    }

    private async Task<bool> SendRawAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (ws.State != WebSocketState.Open) return false;
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            return true;
        }
        catch { return false; }
        finally { _sendLock.Release(); }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
            if (res.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, res.Count);
            if (res.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string ComputeObsAuth(string password, string salt, string challenge)
    {
        static string Sha256B64(string input)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToBase64String(hash);
        }
        var secret = Sha256B64(password + salt);
        return Sha256B64(secret + challenge);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _ws?.Dispose(); } catch { }
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
