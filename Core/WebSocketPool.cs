using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace TwitchDropsMiner.Core;

/// <summary>Подписка на топик PubSub с обработчиком сообщений.</summary>
public sealed class WsTopic(string name, long targetId, Func<long, JsonNode, Task> process) : IEquatable<WsTopic>
{
    public string Id { get; } = Topics.Make(name, targetId);
    public Task Process(JsonNode message) => process(targetId, message);
    public bool Equals(WsTopic? other) => other is not null && other.Id == Id;
    public override bool Equals(object? obj) => Equals(obj as WsTopic);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => Id;
}

/// <summary>Одно websocket-соединение с pubsub-edge.twitch.tv.</summary>
public sealed class TwitchWebSocket
{
    private const string Url = "wss://pubsub-edge.twitch.tv/v1";

    private readonly Miner _miner;
    private readonly int _idx;
    private ClientWebSocket? _ws;
    private Task? _handleTask;
    private CancellationTokenSource? _stopCts;
    private readonly AsyncEvent _connected = new();
    private readonly AsyncEvent _wakeup = new();
    private bool _reconnectRequested;
    private bool _topicsChanged;
    private DateTime _nextPing = DateTime.UtcNow;
    private DateTime _maxPong = DateTime.UtcNow + Limits.PingTimeout;

    public Dictionary<string, WsTopic> Topics { get; } = new();
    private readonly HashSet<WsTopic> _submitted = new();

    public int Index => _idx;

    public TwitchWebSocket(Miner miner, int index)
    {
        _miner = miner;
        _idx = index;
        SetStatus(L.T("gui.websocket.disconnected", "Disconnected"));
    }

    private void SetStatus(string? status = null, bool refreshTopics = false) =>
        _miner.Ui.SetWebsocketStatus(_idx, status, refreshTopics ? Topics.Count : null);

    private void RequestReconnect()
    {
        _nextPing = DateTime.UtcNow;  // после переподключения сразу отправим PING
        _reconnectRequested = true;
        _wakeup.Set();
    }

    public async Task StartAsync()
    {
        StartNoWait();
        await _connected.WaitAsync(_miner.Token);
    }

    public void StartNoWait()
    {
        if (_handleTask is null || _handleTask.IsCompleted)
        {
            _stopCts = CancellationTokenSource.CreateLinkedTokenSource(_miner.Token);
            _handleTask = HandleAsync(_stopCts.Token);
        }
    }

    public async Task StopAsync(bool remove = false)
    {
        if (_stopCts is not null && !_stopCts.IsCancellationRequested)
        {
            if (!remove || !_miner.Websocket.Sockets.Any(s => s.Index == _idx))
                SetStatus(L.T("gui.websocket.disconnecting", "Disconnecting..."));
            _stopCts.Cancel();
            try { _ws?.Abort(); } catch { /* уже закрыт */ }
            if (_handleTask is not null)
            {
                try { await _handleTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* таймаут/отмена */ }
            }
            _handleTask = null;
        }
        if (!remove || !_miner.Websocket.Sockets.Any(s => s.Index == _idx))
            SetStatus(L.T("gui.websocket.disconnected", "Disconnected"));
        if (remove)
        {
            Topics.Clear();
            _topicsChanged = true;
            // строку в интерфейсе убираем, только если этот индекс не занял новый сокет
            if (!_miner.Websocket.Sockets.Any(s => s.Index == _idx)) _miner.Ui.RemoveWebsocket(_idx);
        }
    }

    public void StopNoWait(bool remove = false) => _ = StopAsync(remove);

    private async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            SetStatus(L.T("gui.websocket.initializing", "Initializing..."));
            await _miner.Auth.WaitUntilLoginAsync(ct);
            SetStatus(L.T("gui.websocket.connecting", "Connecting..."));
            Log.Ws($"Websocket[{_idx}] connecting...");
            var backoff = new ExponentialBackoff(maximum: 180);
            while (!ct.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                ws.Options.Proxy = _miner.Http.Proxy;
                ws.Options.KeepAliveInterval = TimeSpan.Zero;  // у PubSub свой PING/PONG
                try
                {
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(_miner.Http.TotalTimeout);
                    await ws.ConnectAsync(new Uri(Url), connectCts.Token);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    double delay = backoff.Next();
                    Log.Ws($"Websocket[{_idx}] connection problem (sleep: {Math.Round(delay)}s): {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    continue;
                }
                backoff.Reset();
                _ws = ws;
                _reconnectRequested = false;
                _connected.Set();
                SetStatus(L.T("gui.websocket.connected", "Connected"));
                Log.Ws($"Websocket[{_idx}] connected.");
                try
                {
                    await RunConnectionAsync(ws, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Ws($"Exception in Websocket[{_idx}]: {ex.Message}");
                }
                finally
                {
                    _ws = null;
                    _connected.Clear();
                    _submitted.Clear();
                    _topicsChanged = true;  // при новом подключении подпишемся заново
                }
                if (ct.IsCancellationRequested) break;
                SetStatus(L.T("gui.websocket.reconnecting", "Reconnecting..."));
                Log.Ws($"Websocket[{_idx}] reconnecting...");
            }
        }
        catch (OperationCanceledException)
        {
            Log.Ws($"Websocket[{_idx}] stopped.");
        }
        catch (Exception ex)
        {
            Log.Error($"Websocket[{_idx}] fatal: {ex}");
        }
    }

    private async Task RunConnectionAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var recvTask = ReceiveLoopAsync(ws, ct);
        Task? wakeTask = null;  // одно ожидание пробуждения, пересоздаётся только после срабатывания
        while (!_reconnectRequested && !ct.IsCancellationRequested)
        {
            await HandlePingAsync(ws, ct);
            await HandleTopicsAsync(ws, ct);
            if (wakeTask is null || wakeTask.IsCompleted)
            {
                _wakeup.Clear();
                wakeTask = _wakeup.WaitAsync(ct);
            }
            var done = await Task.WhenAny(recvTask, wakeTask, Task.Delay(1000, ct));
            if (done == recvTask)
            {
                await recvTask;  // пробросить исключение, если было
                Log.Ws($"Websocket[{_idx}] closed unexpectedly: {ws.CloseStatus}");
                return;
            }
        }
        try
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token);
        }
        catch { /* соединение уже разорвано */ }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            Log.Ws($"Websocket[{_idx}] received: {text}");
            JsonNode? message;
            try { message = JsonNode.Parse(text); }
            catch { continue; }
            HandleMessage(message);
        }
    }

    private void HandleMessage(JsonNode? message)
    {
        switch (message.Str("type"))
        {
            case "MESSAGE":
                var topicId = message.Str("data", "topic");
                if (topicId is not null && Topics.TryGetValue(topicId, out var topic))
                {
                    var inner = message.Str("data", "message");
                    if (inner is null) return;
                    try
                    {
                        var payload = JsonNode.Parse(inner)!;
                        _ = RunHandlerAsync(topic, payload);
                    }
                    catch (Exception ex) { Log.Error($"Bad websocket payload: {ex.Message}"); }
                }
                break;
            case "PONG":
                _maxPong = _nextPing;
                break;
            case "RESPONSE":
                var err = message.Str("error");
                if (!string.IsNullOrEmpty(err)) Log.Ws($"Websocket[{_idx}] response error: {err}");
                break;
            case "RECONNECT":
                Log.Ws($"Websocket[{_idx}] requested reconnect.");
                RequestReconnect();
                break;
            default:
                Log.Ws($"Websocket[{_idx}] received unknown payload: {message?.ToJsonString()}");
                break;
        }
    }

    private static async Task RunHandlerAsync(WsTopic topic, JsonNode payload)
    {
        try { await topic.Process(payload); }
        catch (OperationCanceledException) { }
        catch (ReloadRequestException) { }
        catch (Exception ex) { Log.Error($"Exception in topic handler {topic}: {ex}"); }
    }

    private async Task HandlePingAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now >= _nextPing)
        {
            _nextPing = now + Limits.PingInterval;
            _maxPong = now + Limits.PingTimeout;
            await SendAsync(ws, new JsonObject { ["type"] = "PING" }, ct);
        }
        else if (now >= _maxPong)
        {
            Log.Ws($"Websocket[{_idx}] didn't receive a PONG, reconnecting...");
            RequestReconnect();
        }
    }

    private async Task HandleTopicsAsync(ClientWebSocket ws, CancellationToken ct)
    {
        if (!_topicsChanged) return;
        _topicsChanged = false;
        SetStatus(refreshTopics: true);
        var token = _miner.Auth.AccessToken;
        var current = Topics.Values.ToHashSet();
        var removed = _submitted.Except(current).ToList();
        foreach (var chunk in removed.Select(t => t.Id).Chunk(20))
        {
            await SendAsync(ws, new JsonObject
            {
                ["type"] = "UNLISTEN",
                ["data"] = new JsonObject { ["topics"] = new JsonArray(chunk.Select(t => (JsonNode)t).ToArray()), ["auth_token"] = token },
            }, ct);
        }
        _submitted.ExceptWith(removed);
        var added = current.Except(_submitted).ToList();
        foreach (var chunk in added.Select(t => t.Id).Chunk(20))
        {
            await SendAsync(ws, new JsonObject
            {
                ["type"] = "LISTEN",
                ["data"] = new JsonObject { ["topics"] = new JsonArray(chunk.Select(t => (JsonNode)t).ToArray()), ["auth_token"] = token },
            }, ct);
        }
        _submitted.UnionWith(added);
    }

    private async Task SendAsync(ClientWebSocket ws, JsonObject message, CancellationToken ct)
    {
        if (message.Str("type") != "PING") message["nonce"] = Util.Nonce(Util.CharsAscii, 30);
        var bytes = Encoding.UTF8.GetBytes(J.Minify(message));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        Log.Ws($"Websocket[{_idx}] sent: {(message.Str("type") == "PING" ? "PING" : message.Str("type"))}");
    }

    /// <summary>Забирает из набора столько топиков, сколько помещается (набор изменяется).</summary>
    public void AddTopics(HashSet<WsTopic> topics)
    {
        bool changed = false;
        while (topics.Count > 0 && Topics.Count < Limits.WsTopicsLimit)
        {
            var t = topics.First();
            topics.Remove(t);
            Topics[t.Id] = t;
            changed = true;
        }
        if (changed) { _topicsChanged = true; _wakeup.Set(); }
    }

    public void RemoveTopics(HashSet<string> topics)
    {
        var existing = topics.Where(Topics.ContainsKey).ToList();
        if (existing.Count == 0) return;
        foreach (var t in existing) { topics.Remove(t); Topics.Remove(t); }
        _topicsChanged = true;
        _wakeup.Set();
    }
}

/// <summary>Пул websocket-соединений: распределяет топики по соединениям (до 50 на соединение).</summary>
public sealed class WebSocketPool(Miner miner)
{
    private bool _running;
    public List<TwitchWebSocket> Sockets { get; } = [];

    public async Task StartAsync()
    {
        _running = true;
        await Task.WhenAll(Sockets.Select(ws => ws.StartAsync()));
    }

    public async Task StopAsync(bool clearTopics = false)
    {
        _running = false;
        await Task.WhenAll(Sockets.Select(ws => ws.StopAsync(clearTopics)));
        if (clearTopics) Sockets.Clear();
    }

    public void AddTopics(IEnumerable<WsTopic> topics)
    {
        var set = topics.ToHashSet();
        foreach (var ws in Sockets) set.ExceptWith(ws.Topics.Values);
        if (set.Count == 0) return;
        for (int i = 0; i < Limits.MaxWebsockets; i++)
        {
            TwitchWebSocket ws;
            if (i < Sockets.Count) ws = Sockets[i];
            else
            {
                ws = new TwitchWebSocket(miner, i);
                if (_running) ws.StartNoWait();
                Sockets.Add(ws);
            }
            ws.AddTopics(set);
            if (set.Count == 0) return;
        }
        throw new MinerException("Maximum topics limit has been reached");
    }

    public void RemoveTopics(IEnumerable<string> topics)
    {
        var set = topics.ToHashSet();
        if (set.Count == 0) return;
        foreach (var ws in Sockets) ws.RemoveTopics(set);
        // если соединений больше, чем нужно — останавливаем последнее, его топики раздаём остальным
        var recycled = new List<WsTopic>();
        while (Sockets.Count > 0)
        {
            int count = Sockets.Sum(ws => ws.Topics.Count);
            if (count > (Sockets.Count - 1) * Limits.WsTopicsLimit) break;
            var last = Sockets[^1];
            Sockets.RemoveAt(Sockets.Count - 1);
            recycled.AddRange(last.Topics.Values);
            last.StopNoWait(remove: true);
        }
        if (recycled.Count > 0) AddTopics(recycled);
    }
}
