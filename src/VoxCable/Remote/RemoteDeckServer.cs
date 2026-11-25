using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VoxCable.Remote;

public sealed record RemoteDeckState(
    bool IsRunning,
    bool MuteOutput,
    bool MasterBypass,
    bool HoldToBleep,
    string? ActivePreset,
    IReadOnlyList<string> Presets,
    IReadOnlyList<string> Pads);

public sealed record RemoteDeckCommand(string Action, string? Value = null, int Index = -1, bool Flag = false);

/// <summary>
/// Opt-in zero-dependency local HTTP + WebSocket server ("Redfur Relay Touch Deck")
/// allowing a phone, tablet, or browser on localhost/LAN to act as a live wireless Stream Deck
/// for VoxCable presets, soundboard pads, mute, bypass, and hold-to-bleep.
/// </summary>
public sealed class RemoteDeckServer : IDisposable
{
    private readonly Func<RemoteDeckState> _stateProvider;
    private readonly Action<RemoteDeckCommand> _commandHandler;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _disposed;

    public bool IsRunning { get; private set; }
    public string ActiveUrl { get; private set; } = string.Empty;
    public int ClientCount => _clients.Count;

    public RemoteDeckServer(Func<RemoteDeckState> stateProvider, Action<RemoteDeckCommand> commandHandler)
    {
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
    }

    public string Start(int port = 8765, bool allowLan = false)
    {
        Stop();

        port = Math.Clamp(port, 1024, 65535);
        _cts = new CancellationTokenSource();

        // Try LAN wildcard bind if requested; gracefully fall back to localhost if URLACL is unconfigured
        var listener = new HttpListener();
        bool boundLan = false;
        if (allowLan)
        {
            try
            {
                listener.Prefixes.Add($"http://+:{port}/");
                listener.Start();
                boundLan = true;
            }
            catch
            {
                try { listener.Close(); } catch { }
                listener = new HttpListener();
            }
        }

        if (!boundLan)
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
        }

        _listener = listener;
        IsRunning = true;
        ActiveUrl = boundLan
            ? $"http://{GetLocalLanIp() ?? "localhost"}:{port}/"
            : $"http://localhost:{port}/";

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return ActiveUrl;
    }

    public void BroadcastState()
    {
        if (!IsRunning || _clients.IsEmpty) return;

        string json = JsonSerializer.Serialize(_stateProvider());
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        foreach (var kv in _clients)
        {
            if (kv.Value.State == WebSocketState.Open)
            {
                _ = SendSafeAsync(kv.Key, kv.Value, bytes);
            }
            else
            {
                _clients.TryRemove(kv.Key, out _);
            }
        }
    }

    private async Task SendSafeAsync(Guid id, WebSocket ws, byte[] bytes)
    {
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch
        {
            _clients.TryRemove(id, out _);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleContextAsync(ctx, ct), ct);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (path.Equals("/ws", StringComparison.OrdinalIgnoreCase) && ctx.Request.IsWebSocketRequest)
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                await HandleWebSocketAsync(wsCtx.WebSocket, ct);
                return;
            }

            if (path.Equals("/api/state", StringComparison.OrdinalIgnoreCase))
            {
                byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_stateProvider()));
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = jsonBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(jsonBytes, ct);
                ctx.Response.Close();
                return;
            }

            // Serve embedded mobile Touch Deck PWA
            byte[] htmlBytes = Encoding.UTF8.GetBytes(DeckHtml);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = htmlBytes.Length;
            await ctx.Response.OutputStream.WriteAsync(htmlBytes, ct);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private async Task HandleWebSocketAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients[id] = ws;

        try
        {
            // Push initial state immediately
            byte[] initBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_stateProvider()));
            await ws.SendAsync(initBytes, WebSocketMessageType.Text, true, ct);

            var recvBuf = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(recvBuf, ct);
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.MessageType == WebSocketMessageType.Text && res.Count > 0)
                {
                    string text = Encoding.UTF8.GetString(recvBuf, 0, res.Count);
                    var cmd = JsonSerializer.Deserialize<RemoteDeckCommand>(text,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (cmd != null && !string.IsNullOrWhiteSpace(cmd.Action))
                    {
                        _commandHandler(cmd);
                    }
                }
            }
        }
        catch
        {
            // Client disconnected
        }
        finally
        {
            _clients.TryRemove(id, out _);
            try { ws.Dispose(); } catch { }
        }
    }

    public void Stop()
    {
        IsRunning = false;
        ActiveUrl = string.Empty;

        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;

        foreach (var kv in _clients)
        {
            try { kv.Value.Abort(); } catch { }
            try { kv.Value.Dispose(); } catch { }
        }
        _clients.Clear();

        _cts?.Dispose();
        _cts = null;
        _acceptLoop = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static string? GetLocalLanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private const string DeckHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no" />
<title>VoxCable Touch Deck</title>
<style>
  :root { --bg:#0d1117; --card:#161b26; --bdr:#252e40; --cyan:#38bdf8; --red:#e5484d; --green:#22c55e; --amber:#f59e0b; --txt:#f1f5f9; --mut:#8b9bb4; }
  * { box-sizing:border-box; -webkit-tap-highlight-color:transparent; user-select:none; }
  body { margin:0; padding:14px; background:var(--bg); color:var(--txt); font-family:system-ui,-apple-system,Segoe UI,sans-serif; }
  .top { display:flex; align-items:center; justify-content:space-between; margin-bottom:12px; }
  .title { font-size:18px; font-weight:800; letter-spacing:1px; }
  .pill { font-size:12px; font-weight:700; padding:4px 10px; border-radius:99px; background:#1e2534; border:1px solid var(--bdr); color:var(--mut); }
  .pill.live { color:var(--green); border-color:var(--green); }
  .row { display:grid; grid-template-columns:repeat(4,1fr); gap:8px; margin-bottom:14px; }
  .btn { background:var(--card); border:1px solid var(--bdr); color:var(--txt); border-radius:10px; padding:14px 8px; font-weight:700; font-size:13px; text-align:center; cursor:pointer; transition:transform .06s, border-color .15s; }
  .btn:active { transform:scale(.96); }
  .btn.on-red { background:#451218; border-color:var(--red); color:#fecaca; }
  .btn.on-amber { background:#3b2608; border-color:var(--amber); color:#fde68a; }
  .btn.on-green { background:#113a21; border-color:var(--green); color:#bbf7d0; }
  .btn.on-cyan { background:#102b40; border-color:var(--cyan); color:#bae6fd; }
  h3 { font-size:11px; letter-spacing:1.2px; color:var(--mut); margin:12px 0 8px; text-transform:uppercase; }
  .presets { display:grid; grid-template-columns:repeat(auto-fill,minmax(120px,1fr)); gap:8px; }
  .pads { display:grid; grid-template-columns:repeat(4,1fr); gap:8px; }
  .pad { min-height:68px; display:flex; align-items:center; justify-content:center; padding:8px; background:#1b2231; border:1px solid #2d3950; border-radius:10px; color:var(--txt); font-weight:600; font-size:12.5px; text-align:center; }
  .pad:active { background:#1e3a5f; border-color:var(--cyan); transform:scale(.96); }
  @media(max-width:560px){ .pads{ grid-template-columns:repeat(2,1fr);} .row{ grid-template-columns:repeat(2,1fr);} }
</style>
</head>
<body>
  <div class="top">
    <div class="title">VOXCABLE <span style="color:var(--cyan);font-size:12px">TOUCH DECK</span></div>
    <div id="status" class="pill">CONNECTING...</div>
  </div>
  <div class="row">
    <div id="btnConn" class="btn" onclick="send({action:'toggleConnect'})">CONNECT</div>
    <div id="btnMute" class="btn" onclick="send({action:'toggleMute'})">MUTE</div>
    <div id="btnByp" class="btn" onclick="send({action:'toggleBypass'})">BYPASS FX</div>
    <div id="btnBleep" class="btn"
         onpointerdown="send({action:'setBleep',flag:true})"
         onpointerup="send({action:'setBleep',flag:false})"
         onpointerleave="send({action:'setBleep',flag:false})">HOLD BLEEP</div>
  </div>
  <h3>Voice Presets</h3>
  <div id="presets" class="presets"></div>
  <div style="display:flex;justify-content:space-between;align-items:center;margin-top:14px;">
    <h3 style="margin:0">Soundboard Pads</h3>
    <div class="btn" style="padding:6px 12px;font-size:11px" onclick="send({action:'stopAll'})">STOP ALL SFX</div>
  </div>
  <div id="pads" class="pads" style="margin-top:8px"></div>
<script>
let ws;
function connect(){
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  ws = new WebSocket(`${proto}//${location.host}/ws`);
  ws.onopen = () => { document.getElementById('status').textContent = 'LINKED'; };
  ws.onclose = () => { document.getElementById('status').textContent = 'RECONNECTING...'; setTimeout(connect, 1500); };
  ws.onmessage = (e) => render(JSON.parse(e.data));
}
function send(obj){ if(ws && ws.readyState === 1) ws.send(JSON.stringify(obj)); }
function render(s){
  const st = document.getElementById('status');
  st.textContent = s.IsRunning ? 'LIVE' : 'STANDBY';
  st.className = 'pill ' + (s.IsRunning ? 'live' : '');
  const bc = document.getElementById('btnConn');
  bc.textContent = s.IsRunning ? 'DISCONNECT' : 'CONNECT';
  bc.className = 'btn ' + (s.IsRunning ? 'on-green' : '');
  document.getElementById('btnMute').className = 'btn ' + (s.MuteOutput ? 'on-red' : '');
  document.getElementById('btnByp').className = 'btn ' + (s.MasterBypass ? 'on-amber' : '');
  document.getElementById('btnBleep').className = 'btn ' + (s.HoldToBleep ? 'on-red' : '');
  const pEl = document.getElementById('presets');
  pEl.innerHTML = '';
  (s.Presets || []).forEach(name => {
    const d = document.createElement('div');
    d.className = 'btn ' + (s.ActivePreset === name ? 'on-cyan' : '');
    d.textContent = name;
    d.onclick = () => send({action:'applyPreset', value:name});
    pEl.appendChild(d);
  });
  const sEl = document.getElementById('pads');
  sEl.innerHTML = '';
  (s.Pads || []).forEach((name, idx) => {
    const d = document.createElement('div');
    d.className = 'pad';
    d.textContent = name;
    d.onclick = () => send({action:'triggerPad', index:idx});
    sEl.appendChild(d);
  });
}
connect();
</script>
</body>
</html>
""";
}
