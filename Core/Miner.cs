using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TwitchDropsMiner.Core;

/// <summary>
/// Ядро приложения (порт класса Twitch из twitch.py).
/// Вся логика выполняется в UI-потоке (как однопоточный asyncio в оригинале),
/// сетевой ввод-вывод и разбор JSON — в пуле потоков, поэтому интерфейс не подвисает.
/// </summary>
public sealed class Miner
{
    public Settings Settings { get; }
    public IMinerUi Ui { get; }
    /// <summary>Клиент Twitch; выбирается при входе и сохраняется вместе с токеном.</summary>
    public ClientInfo Client { get; set; } = ClientType.SmartBox;
    public AuthState Auth { get; }
    public WebSocketPool Websocket { get; }
    public ProgressTracker Progress { get; } = new();
    public ImageCache Images { get; }

    private HttpService? _http;
    public HttpService Http => _http ??= new HttpService(Settings, () => Client, Ui.Print, Token);

    private readonly CancellationTokenSource _exitCts = new();
    public CancellationToken Token => _exitCts.Token;
    public bool CloseRequested => _exitCts.IsCancellationRequested;

    // Состояние
    private MinerState _state = MinerState.Idle;
    private readonly AsyncEvent _stateChange = new();
    public List<Game> WantedGames { get; } = [];
    public List<DropsCampaign> Inventory { get; } = [];
    private readonly Dictionary<string, TimedDrop> _drops = new();
    public Dictionary<string, DropsCampaign> CampaignsById { get; } = new();
    private readonly List<DateTimeOffset> _mntTriggers = [];
    // GQL очень чувствителен к лимитам: не более 5 запросов в секунду
    private readonly RateLimiter _gqlLimiter = new(5, TimeSpan.FromSeconds(1));

    // Каналы
    public OrderedDictionary<long, Channel> Channels { get; } = new();
    public AwaitableValue<Channel> WatchingChannel { get; } = new();
    private Task? _watchTask;
    private CancellationTokenSource? _runCts;
    private readonly AsyncEvent _watchingRestart = new();
    private bool _estimatedWarned;
    private Task? _mntTask;
    private CancellationTokenSource? _mntCts;

    public Miner(Settings settings, IMinerUi ui)
    {
        Settings = settings;
        Ui = ui;
        Auth = new AuthState(this);
        Websocket = new WebSocketPool(this);
        Images = new ImageCache(() => _http);
    }

    #region Управление состоянием

    public MinerState State => _state;

    public void ChangeState(MinerState state)
    {
        if (_state != MinerState.Exit) _state = state;  // из EXIT выйти нельзя
        _stateChange.Set();
    }

    /// <summary>Пользователь закрыл приложение.</summary>
    public void Close()
    {
        ChangeState(MinerState.Exit);
        _exitCts.Cancel();
    }

    public void Print(string message) => Ui.Print(message);

    public void Save() => Settings.Save();

    #endregion

    #region Главный цикл

    public async Task RunAsync()
    {
        if (Settings.ArgDump) File.WriteAllText(AppPaths.DumpFile, "");
        while (!CloseRequested)
        {
            try
            {
                await RunInternalAsync();
                break;
            }
            catch (ReloadRequestException)
            {
                await ShutdownAsync();
            }
            catch (OperationCanceledException) when (CloseRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // В оригинале приложение останавливалось. Здесь — пишем ошибку и перезапускаемся через минуту.
                Print($"{L.T("x.fatal", "Fatal error:")} {ex.Message}");
                Log.Error(ex.ToString());
                Ui.SetTrayIcon(TrayIconState.Error);
                Ui.SetStatus($"{L.T("x.fatal", "Fatal error:")} {ex.Message}");
                await ShutdownAsync();
                try { await Task.Delay(TimeSpan.FromSeconds(60), Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        Print(L.T("gui.status.exiting", "Exiting..."));
        await ShutdownAsync();
        Save();
    }

    public async Task ShutdownAsync()
    {
        var start = DateTime.UtcNow;
        StopWatching();
        _runCts?.Cancel();
        _runCts = null;
        _watchTask = null;
        _mntCts?.Cancel();
        _mntCts = null;
        _mntTask = null;
        await Websocket.StopAsync(clearTopics: true);
        _http?.Dispose();
        _http = null;
        _drops.Clear();
        foreach (var ch in Channels.Values) ch.Remove();
        Channels.Clear();
        Inventory.Clear();
        Auth.Clear();
        WantedGames.Clear();
        _mntTriggers.Clear();
        var wait = start + TimeSpan.FromSeconds(0.5) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
    }

    private async Task RunInternalAsync()
    {
        _state = MinerState.Idle;
        await Auth.ValidateAsync(Token);
        await Websocket.StartAsync();
        // цикл просмотра перезапускается при каждом новом запуске
        _runCts?.Cancel();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _watchTask = WatchLoopAsync(_runCts.Token);
        Websocket.AddTopics([
            new WsTopic(Topics.UserDrops, Auth.UserId, ProcessDropsAsync),
            new WsTopic(Topics.UserNotifications, Auth.UserId, ProcessNotificationsAsync),
        ]);
        bool fullCleanup = false;
        ChangeState(MinerState.InventoryFetch);
        while (true)
        {
            Token.ThrowIfCancellationRequested();
            switch (_state)
            {
                case MinerState.Idle:
                    if (Settings.ArgDump) { Ui.Print("Dump finished"); Close(); continue; }
                    Ui.SetTrayIcon(TrayIconState.Idle);
                    Ui.SetStatus(L.T("gui.status.idle", "Idle"));
                    StopWatching();
                    _stateChange.Clear();  // ждём следующего изменения состояния
                    break;

                case MinerState.InventoryFetch:
                    Ui.SetTrayIcon(TrayIconState.Maint);
                    await Websocket.StartAsync();
                    await FetchInventoryAsync();
                    Ui.SetGames(Inventory.Select(c => c.Game).Distinct());
                    Save();
                    ChangeState(MinerState.GamesUpdate);
                    break;

                case MinerState.GamesUpdate:
                    // забираем готовые награды из активных и завершившихся кампаний
                    foreach (var campaign in Inventory.ToList())
                    {
                        if (campaign.Upcoming) continue;
                        foreach (var drop in campaign.Drops.ToList())
                            if (drop.CanClaim) await drop.ClaimAsync();
                    }
                    ComputeWantedGames();
                    fullCleanup = true;
                    RestartWatching();
                    ChangeState(MinerState.ChannelsCleanup);
                    break;

                case MinerState.ChannelsCleanup:
                    Ui.SetStatus(L.T("gui.status.cleanup", "Cleaning up channels..."));
                    List<Channel> toRemove = WantedGames.Count == 0 || fullCleanup
                        ? Channels.Values.ToList()
                        : Channels.Values.Where(ch => !ch.AclBased
                            && (ch.Offline || ch.Game is null || !WantedGames.Contains(ch.Game))).ToList();
                    fullCleanup = false;
                    if (toRemove.Count > 0)
                    {
                        Websocket.RemoveTopics(toRemove.SelectMany(ChannelTopicIds));
                        foreach (var ch in toRemove)
                        {
                            Channels.Remove(ch.Id);
                            ch.Remove();
                        }
                    }
                    if (WantedGames.Count > 0) ChangeState(MinerState.ChannelsFetch);
                    else
                    {
                        Print(L.T("status.no_campaign", "No active campaigns to mine drops for. Waiting for an active campaign..."));
                        ChangeState(MinerState.Idle);
                    }
                    break;

                case MinerState.ChannelsFetch:
                    await FetchChannelsAsync();
                    ChangeState(MinerState.ChannelSwitch);
                    break;

                case MinerState.ChannelSwitch:
                    if (Settings.ArgDump) { Ui.Print("Dump finished"); Close(); continue; }
                    SwitchChannel();
                    break;

                case MinerState.Restart:
                    throw new ReloadRequestException();

                case MinerState.Exit:
                    Ui.SetTrayIcon(TrayIconState.Pickaxe);
                    Ui.SetStatus(L.T("gui.status.exiting", "Exiting..."));
                    return;
            }
            await _stateChange.WaitAsync(Token);
        }
    }

    private static IEnumerable<string> ChannelTopicIds(Channel ch) =>
    [
        Topics.Make(Topics.ChannelStreamState, ch.Id),
        Topics.Make(Topics.ChannelStreamUpdate, ch.Id),
    ];

    /// <summary>Определяем, какие игры добывать и в каком порядке.</summary>
    private void ComputeWantedGames()
    {
        WantedGames.Clear();
        var exclude = Settings.Exclude;
        var priority = Settings.Priority;
        var mode = Settings.PriorityMode;
        bool priorityOnly = mode == PriorityMode.PriorityOnly;
        var nextHour = DateTimeOffset.UtcNow.AddHours(1);
        IEnumerable<DropsCampaign> sorted = Inventory;
        if (mode == PriorityMode.EndingSoonest) sorted = sorted.OrderBy(c => c.EndsAt);
        else if (mode == PriorityMode.LowAvailabilityFirst) sorted = sorted.OrderBy(c => c.Availability);
        // стабильная сортировка: игры из списка приоритета — первыми, в порядке списка
        sorted = sorted.OrderBy(c => priority.IndexOf(c.Game.Name) is var i and >= 0 ? i : int.MaxValue).ToList();
        foreach (var campaign in sorted)
        {
            var game = campaign.Game;
            if (!WantedGames.Contains(game)
                && !exclude.Contains(game.Name)
                && (!priorityOnly || priority.Contains(game.Name))
                && campaign.CanEarnWithin(nextHour))
            {
                WantedGames.Add(game);
            }
        }
    }

    private async Task FetchChannelsAsync()
    {
        Ui.SetStatus(L.T("gui.status.gathering", "Gathering channels..."));
        var newChannels = Channels.Values.ToHashSet();
        Channels.Clear();
        Ui.ChannelsClear();
        // каналы из ACL кампаний (только кампании, которые можно продвинуть)
        var noAcl = new HashSet<Game>();
        var aclChannels = new HashSet<Channel>();
        var nextHour = DateTimeOffset.UtcNow.AddHours(1);
        foreach (var campaign in Inventory)
        {
            if (WantedGames.Contains(campaign.Game) && campaign.CanEarnWithin(nextHour))
            {
                if (campaign.AllowedChannels.Count > 0) aclChannels.UnionWith(campaign.AllowedChannels);
                else noAcl.Add(campaign.Game);
            }
        }
        aclChannels.ExceptWith(newChannels);
        await BulkCheckOnlineAsync(aclChannels);
        newChannels.UnionWith(aclChannels);
        // для игр без ACL — живые каналы с включёнными дропами
        var liveTasks = noAcl.Select(g => GetLiveStreamsAsync(g, dropsEnabled: true)).ToList();
        foreach (var list in await Task.WhenAll(liveTasks)) newChannels.UnionWith(list);

        // сортировка: приоритет игры → ACL → зрители (онлайн-каналы выше)
        var ordered = newChannels
            .OrderBy(GetPriority)
            .ThenByDescending(ch => ch.AclBased)
            .ThenByDescending(ch => ch.Viewers ?? -1)
            .ToList();
        var trimmed = ordered.Skip(Limits.MaxChannels).ToList();
        ordered = ordered.Take(Limits.MaxChannels).ToList();
        if (trimmed.Count > 0) Websocket.RemoveTopics(trimmed.SelectMany(ChannelTopicIds));
        foreach (var ch in ordered)
        {
            Channels[ch.Id] = ch;
            ch.Display(add: true);
        }
        Websocket.AddTopics(Channels.Keys.SelectMany(id => new[]
        {
            new WsTopic(Topics.ChannelStreamState, id, ProcessStreamStateAsync),
            new WsTopic(Topics.ChannelStreamUpdate, id, ProcessStreamUpdateAsync),
        }));
        // перепривязываем просматриваемый канал или прекращаем просмотр
        var watching = WatchingChannel.Value;
        if (watching is not null)
        {
            if (Channels.TryGetValue(watching.Id, out var newWatching) && CanWatch(newWatching))
                Watch(newWatching, updateStatus: false);
            else
                StopWatching();
        }
        // заранее показываем активный дроп (минус минута)
        foreach (var ch in Channels.Values)
        {
            if (!CanWatch(ch)) continue;
            GetActiveCampaign(ch)?.FirstDrop?.Display(countdown: false, subOne: true);
            break;
        }
    }

    private void SwitchChannel()
    {
        Ui.SetStatus(L.T("gui.status.switching", "Switching the channel..."));
        Channel? newWatching = null;
        if (Ui.SelectedChannelId is long selId && Channels.TryGetValue(selId, out var selected) && CanWatch(selected))
        {
            // выбранный пользователем канал — в первую очередь
            newWatching = selected;
        }
        else
        {
            newWatching = Channels.Values.OrderBy(GetPriority).FirstOrDefault(ShouldSwitch);
        }
        var watching = WatchingChannel.Value;
        if (newWatching is not null)
        {
            Watch(newWatching);
            _stateChange.Clear();
        }
        else if (watching is not null && CanWatch(watching))
        {
            Ui.SetStatus(L.F("status.watching", "Watching: {channel}", ("channel", watching.Name)));
            _stateChange.Clear();
        }
        else
        {
            Print(L.T("status.no_channel", "No available channels to watch. Waiting for an ONLINE channel..."));
            ChangeState(MinerState.Idle);
        }
    }

    #endregion

    #region Просмотр

    /// <summary>Приоритет канала: 0 — наивысший, int.MaxValue — игра не нужна.</summary>
    public int GetPriority(Channel channel)
    {
        var game = channel.Game;
        if (game is null) return int.MaxValue;
        int idx = WantedGames.IndexOf(game);
        return idx < 0 ? int.MaxValue : idx;
    }

    public bool CanWatch(Channel channel)
    {
        if (!channel.Online) return false;
        foreach (var campaign in Inventory)
        {
            if (campaign.CanEarn(channel)
                && ((channel.Game is not null && channel.DropsEnabled && WantedGames.Contains(channel.Game))
                    || campaign.Game.IsSpecial))
                return true;
        }
        return false;
    }

    public bool ShouldSwitch(Channel channel)
    {
        if (!CanWatch(channel)) return false;
        var watching = WatchingChannel.Value;
        if (watching is null || !CanWatch(watching)) return true;
        int channelOrder = GetPriority(channel);
        int watchingOrder = GetPriority(watching);
        return channelOrder < watchingOrder
               || (channelOrder == watchingOrder && channel.AclBased && !watching.AclBased);
    }

    private bool _playerWarned;

    public void Watch(Channel channel, bool updateStatus = true)
    {
        if (!UseBrowserPlayer && !_playerWarned)
        {
            _playerWarned = true;
            Print(L.T("x.player_required", "Twitch counts watch time only through its player: log in with the browser to make progress."));
        }
        Ui.SetTrayIcon(TrayIconState.Active);
        Ui.SetWatching(channel);
        WatchingChannel.Set(channel);
        if (UseBrowserPlayer) Ui.BrowserWatch(channel.Login);
        if (updateStatus)
        {
            var text = L.F("status.watching", "Watching: {channel}", ("channel", channel.Name));
            Print(text);
            Ui.SetStatus(text);
        }
    }

    public void StopWatching()
    {
        ClearDrop();
        WatchingChannel.Clear();
        Ui.SetWatching(null);
        Ui.BrowserWatch(null);
    }

    private bool UseBrowserPlayer => Settings.BrowserPlayer && ClientType.IsWeb(Client);
    private DateTime _lastSiteSync = DateTime.MinValue;

    /// <summary>
    /// Реальный прогресс с сайта Twitch (через встроенный браузер): API его больше не отдаёт,
    /// а страница инвентаря Twitch получает его со своей integrity-проверкой.
    /// </summary>
    private async Task SyncSiteProgressAsync()
    {
        if (!ClientType.IsWeb(Client) || DateTime.UtcNow - _lastSiteSync < TimeSpan.FromMinutes(5)) return;
        _lastSiteSync = DateTime.UtcNow;
        var inv = await Ui.FetchSiteInventoryAsync(Token);
        if (inv is null) { Log.Call("Site inventory: no data"); return; }
        int updated = 0;
        foreach (var c in inv.Arr("dropCampaignsInProgress"))
        foreach (var d in c.Arr("timeBasedDrops"))
        {
            if (d.Str("id") is { } id && _drops.TryGetValue(id, out var drop) && d.At("self") is not null)
            {
                drop.UpdateMinutes(d.Int("self", "currentMinutesWatched"));
                updated++;
                Log.Call($"Drop progress from site: {drop.Name} ({drop.Campaign.Game}, {drop.CurrentMinutes}/{drop.RequiredMinutes})");
            }
        }
        if (updated == 0) Log.Call("Site inventory: no campaigns in progress");
    }

    public void RestartWatching()
    {
        Progress.Stop();
        _watchingRestart.Set();
    }

    public void DisplayDrop(TimedDrop drop, bool countdown = true, bool subOne = false)
    {
        if (Progress.Display(drop, countdown, subOne))
        {
            Print($"{L.T("gui.progress.campaign_progress", "Progress:")} {drop.CurrentMinutes}/{drop.RequiredMinutes} - {drop.Campaign.Game.Name}, {drop.Campaign.Name}");
        }
        Ui.DisplayDrop(drop);
    }

    private void ClearDrop()
    {
        Progress.Display(null, false, false);
        Ui.DisplayDrop(null);
    }

    /// <summary>
    /// Каждые ~59 с отправляем событие «минута просмотра».
    /// Если за ~20 с websocket не прислал прогресс — уточняем его через GQL или оцениваем сами.
    /// </summary>
    private async Task WatchLoopAsync(CancellationToken ct)
    {
        double interval = Limits.WatchInterval.TotalSeconds;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var channel = await WatchingChannel.GetAsync(ct);
                if (!channel.Online)
                {
                    StopWatching();
                    continue;
                }
                bool ok = await channel.SendWatchAsync();
                var lastSent = DateTime.UtcNow;
                if (!ok) Log.Call($"Watch requested failed for channel: {channel.Name}");
                bool hls = await channel.SendPlaylistWatchAsync();
                Log.Call($"HLS segment request for {channel.Name}: {(hls ? "OK" : "failed")}");
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                await channel.SendPlaylistWatchAsync();
                if (UseBrowserPlayer) Log.Call($"Browser player state: {await Ui.BrowserPlayerStateAsync() ?? "-"}");
                await SyncSiteProgressAsync();
                if (Progress.MinuteAlmostDone())
                {
                    await UpdateProgressFallbackAsync(channel);
                }
                // оставшееся время до следующей минуты: сегмент потока каждые ~20 с
                _watchingRestart.Clear();
                while (true)
                {
                    double left = interval - (DateTime.UtcNow - lastSent).TotalSeconds;
                    if (left <= 0) break;
                    if (await _watchingRestart.WaitAsync(TimeSpan.FromSeconds(Math.Min(20, left)), ct)) break;
                    if (left > 20 && WatchingChannel.Value == channel) await channel.SendPlaylistWatchAsync();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ReloadRequestException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"Exception in watch loop: {ex}");
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { return; }
            }
        }
    }

    private async Task UpdateProgressFallbackAsync(Channel channel)
    {
        // Способ 1: спросить текущий дроп через GQL
        bool handled = false;
        JsonNode? dropData = null;
        try
        {
            var context = await GqlAsync(Gql.CurrentDrop(channel.Id));
            dropData = context.At("data", "currentUser", "dropCurrentSession");
        }
        catch (GqlException) { }
        if (dropData is not null && _drops.TryGetValue(dropData.Str("dropID") ?? "", out var gqlDrop) && gqlDrop.CanEarn(channel))
        {
            gqlDrop.UpdateMinutes(dropData.Int("currentMinutesWatched"));
            Log.Call($"Drop progress from GQL: {gqlDrop.Name} ({gqlDrop.Campaign.Game}, {gqlDrop.CurrentMinutes}/{gqlDrop.RequiredMinutes})");
            handled = true;
        }
        // Способ 2: прибавить минуту наиболее вероятной кампании
        if (!handled)
        {
            if (!_estimatedWarned && Client == ClientType.SmartBox)
            {
                _estimatedWarned = true;
                Print(L.T("x.progress_estimated", "Twitch does not report progress to this client — estimated progress is shown."));
            }
            var active = GetActiveCampaign(channel);
            if (active is not null)
            {
                active.BumpMinutes(channel);
                var d = active.FirstDrop;
                Log.Call(d is not null
                    ? $"Drop progress from active search: {d.Name} ({d.Campaign.Game}, {d.CurrentMinutes}/{d.RequiredMinutes})"
                    : $"Drop progress from active search: Unknown drop ({active.Game})");
            }
            else Log.Call("No active drop could be determined");
        }
    }

    /// <summary>Раз в час — полная перезагрузка, а в моменты начала/конца кампаний — очистка каналов.</summary>
    private async Task MaintenanceAsync(CancellationToken ct)
    {
        try
        {
            var nextPeriod = DateTimeOffset.UtcNow.AddHours(1);
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= nextPeriod) break;
                var nextTrigger = nextPeriod;
                while (_mntTriggers.Count > 0 && _mntTriggers[0] <= nextTrigger)
                {
                    nextTrigger = _mntTriggers[0];
                    _mntTriggers.RemoveAt(0);
                }
                var kind = nextTrigger == nextPeriod ? "Reload" : "Cleanup";
                Log.Call($"Maintenance task waiting until: {nextTrigger.ToLocalTime():HH:mm:ss} ({kind})");
                var wait = nextTrigger - now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                if (DateTimeOffset.UtcNow >= nextPeriod) break;
                if (nextTrigger != nextPeriod)
                {
                    Log.Call("Maintenance task requests channels cleanup");
                    ChangeState(MinerState.ChannelsCleanup);
                }
            }
            Log.Call("Maintenance task requests a reload");
            ChangeState(MinerState.InventoryFetch);
        }
        catch (OperationCanceledException) { }
    }

    public DropsCampaign? GetActiveCampaign(Channel? channel = null)
    {
        if (WantedGames.Count == 0) return null;
        var watching = WatchingChannel.Value ?? channel;
        if (watching is null) return null;
        return Inventory.Where(c => c.CanEarn(watching)).OrderBy(c => c.RemainingMinutes).FirstOrDefault();
    }

    #endregion

    #region Обработчики websocket

    private Task ProcessStreamStateAsync(long channelId, JsonNode message)
    {
        if (!Channels.TryGetValue(channelId, out var channel))
        {
            Log.Error($"Stream state change for a non-existing channel: {channelId}");
            return Task.CompletedTask;
        }
        switch (message.Str("type"))
        {
            case "viewcount":
                if (!channel.Online) channel.CheckOnline();
                else
                {
                    channel.Viewers = message.Int("viewers");
                    channel.Display();
                }
                break;
            case "stream-down":
                channel.SetOffline();
                break;
            case "stream-up":
                channel.CheckOnline();
                break;
            case "commercial":
                break;
            default:
                Log.Warning($"Unknown stream state: {message.Str("type")}");
                break;
        }
        return Task.CompletedTask;
    }

    private Task ProcessStreamUpdateAsync(long channelId, JsonNode message)
    {
        if (!Channels.TryGetValue(channelId, out var channel))
        {
            Log.Error($"Broadcast settings update for a non-existing channel: {channelId}");
            return Task.CompletedTask;
        }
        var oldGame = message.Str("old_game");
        var newGame = message.Str("game");
        var change = oldGame != newGame ? $", game changed: {oldGame} -> {newGame}" : "";
        Log.Call($"Channel update from websocket: {channel.Name}{change}");
        // данных о тегах тут нет — перепроверяем стрим с задержкой
        channel.CheckOnline();
        return Task.CompletedTask;
    }

    /// <summary>Вызывается каналом при изменении статуса (в сети/не в сети, название, теги).</summary>
    public void OnChannelUpdate(Channel channel, LiveStream? before, LiveStream? after)
    {
        static string Gift(bool b) => b ? "✔" : "❌";
        if (before is null)
        {
            if (after is not null)
            {
                if (ShouldSwitch(channel))
                {
                    Print(L.F("status.goes_online", "{channel} goes ONLINE, switching...", ("channel", channel.Name)));
                    Watch(channel);
                }
                else Log.Info($"{channel.Name} goes ONLINE");
            }
            else Log.Call($"{channel.Name} stays OFFLINE");
        }
        else
        {
            var watching = WatchingChannel.Value;
            if (watching is not null && watching.Equals(channel))
            {
                if (!CanWatch(channel))
                {
                    if (after is null)
                        Print(L.F("status.goes_offline", "{channel} goes OFFLINE, switching...", ("channel", channel.Name)));
                    else
                        Log.Info($"{channel.Name} status has been updated, switching... (🎁: {Gift(before.DropsEnabled)} -> {Gift(after.DropsEnabled)})");
                    ChangeState(MinerState.ChannelSwitch);
                }
            }
            else if (after is null)
            {
                Log.Info($"{channel.Name} goes OFFLINE");
            }
            else
            {
                Log.Info($"{channel.Name} status has been updated (🎁: {Gift(before.DropsEnabled)} -> {Gift(after.DropsEnabled)})");
                if (ShouldSwitch(channel)) Watch(channel);
            }
        }
        channel.Display();
    }

    private async Task ProcessDropsAsync(long userId, JsonNode message)
    {
        var type = message.Str("type");
        if (type is not ("drop-progress" or "drop-claim")) return;
        var dropId = message.Str("data", "drop_id") ?? "";
        _drops.TryGetValue(dropId, out var drop);
        var watching = WatchingChannel.Value;
        if (type == "drop-claim")
        {
            if (drop is null)
            {
                Log.Error($"Received a drop claim ID for a non-existing drop: {dropId}\nDrop claim ID: {message.Str("data", "drop_instance_id")}");
                return;
            }
            drop.ClaimId = message.Str("data", "drop_instance_id");
            var campaign = drop.Campaign;
            await drop.ClaimAsync();
            drop.Display();
            // Через 4–20 с после получения можно начинать следующий дроп — ждём, пока сменится текущий дроп
            await Task.Delay(TimeSpan.FromSeconds(4), Token);
            if (watching is not null)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    var context = await GqlAsync(Gql.CurrentDrop(watching.Id));
                    var dropData = context.At("data", "currentUser", "dropCurrentSession");
                    if (dropData is null || dropData.Str("dropID") != drop.Id) break;
                    await Task.Delay(TimeSpan.FromSeconds(2), Token);
                }
            }
            if (campaign.CanEarn(watching)) RestartWatching();
            else ChangeState(MinerState.InventoryFetch);
            return;
        }
        // drop-progress
        int current = message.Int("data", "current_progress_min");
        int required = message.Int("data", "required_progress_min");
        Log.Call(drop is not null
            ? $"Drop update from websocket: {drop.Name} ({drop.Campaign.Game}, {current}/{required})"
            : "Drop update from websocket: <Unknown>");
        if (drop is not null && drop.CanEarn(WatchingChannel.Value))
            drop.UpdateMinutes(current);
    }

    private async Task ProcessNotificationsAsync(long userId, JsonNode message)
    {
        if (message.Str("type") != "create-notification") return;
        var data = message.At("data", "notification");
        if (data.Str("type") is "user_drop_reward_reminder_notification" or "quests_viewer_reward_campaign_earned_emote")
        {
            ChangeState(MinerState.InventoryFetch);
            await GqlAsync(Gql.NotificationsDelete(data.Str("id") ?? ""));
        }
    }

    #endregion

    #region GQL

    public async Task<JsonNode> GqlAsync(JsonObject op) => await GqlRawAsync(op);

    public async Task<List<JsonNode>> GqlBatchAsync(IReadOnlyList<JsonObject> ops)
    {
        if (ops.Count == 0) return [];
        var resp = await GqlRawAsync(new JsonArray(ops.Select(o => (JsonNode)o.DeepClone()).ToArray()));
        return resp is JsonArray arr ? arr.Where(x => x is not null).Select(x => x!).ToList() : [resp];
    }

    private async Task<JsonNode> GqlRawAsync(JsonNode ops)
    {
        var body = J.Minify(ops);
        Log.GqlLog($"GQL Request: {body}");
        var backoff = new ExponentialBackoff(maximum: 60);
        bool singleRetry = true;
        bool integrityRetry = true;
        while (true)
        {
            double delay = backoff.Next();
            await Auth.ValidateAsync(Token);
            await Auth.EnsureIntegrityAsync(force: false);
            JsonNode? response;
            using (await _gqlLimiter.AcquireAsync(Token))
            {
                (_, response) = await Http.RequestJsonAsync(HttpMethod.Post, "https://gql.twitch.tv/gql",
                    () => new StringContent(body, Encoding.UTF8, "application/json"),
                    h => Auth.ApplyHeaders(h, gql: true));
            }
            if (response is null) throw new GqlException("Empty GQL response");
            Log.GqlLog($"GQL Response: {response.ToJsonString()}");
            var list = response is JsonArray a ? a.Where(x => x is not null).Select(x => x!).ToList() : [response];
            bool forceRetry = false;
            foreach (var r in list)
            {
                if (r["errors"] is JsonArray errors)
                {
                    bool handled = false;
                    foreach (var err in errors)
                    {
                        var msg = err.Str("message");
                        if (msg is null) continue;
                        if (singleRetry && msg is "service error" or "PersistedQueryNotFound")
                        {
                            Log.Error($"Retrying a {msg} for {r.Str("extensions", "operationName")}");
                            singleRetry = false;
                            delay = Math.Max(delay, 5);
                            forceRetry = handled = true;
                            break;
                        }
                        if (integrityRetry && msg == "failed integrity check" && ClientType.IsWeb(Client))
                        {
                            // integrity-токен устарел — берём новый из браузера и повторяем
                            integrityRetry = false;
                            await Auth.EnsureIntegrityAsync(force: true);
                            delay = 1;
                            forceRetry = handled = true;
                            break;
                        }
                        if (msg is "server error" or "failed integrity check")
                        {
                            // обнуляем ключ, на который указывает путь ошибки
                            var path = err.Arr("path").Select(p => p.ToString()).ToList();
                            if (path.Count > 0 && r["data"] is JsonObject dataObj)
                            {
                                JsonNode? node = dataObj;
                                foreach (var key in path.SkipLast(1))
                                    node = int.TryParse(key, out var idx) ? node.At(idx) : node.At(key);
                                if (node is JsonObject target) target[path[^1]] = null;
                            }
                            handled = true;
                            break;
                        }
                        if (msg is "service timeout" or "request cancelled" or "service unavailable" or "context deadline exceeded")
                        {
                            forceRetry = handled = true;
                            break;
                        }
                    }
                    if (!handled) throw new GqlException(errors.ToJsonString());
                }
                else if (r["error"] is not null)
                {
                    if (r.Int("status") == 401)
                    {
                        // токен отозван — нужен повторный вход
                        Auth.Invalidate(deleteStored: true);
                        ChangeState(MinerState.Restart);
                    }
                    throw new GqlException($"{r.Str("error")}: {r.Str("message")}");
                }
                if (forceRetry) break;
            }
            if (!forceRetry) return response;
            await Task.Delay(TimeSpan.FromSeconds(delay), Token);
        }
    }

    #endregion

    #region Инвентарь и каналы

    private async Task<JsonObject> FetchCampaignsAsync(List<KeyValuePair<string, JsonObject>> chunk)
    {
        var responses = await GqlBatchAsync(chunk
            .Select(kv => Gql.CampaignDetails(Auth.UserId.ToString(), kv.Key)).ToList());
        var fetched = new JsonObject();
        foreach (var r in responses)
        {
            if (r.At("data", "user", "dropCampaign") is JsonObject c && c.Str("id") is { } id)
                fetched[id] = c.DeepClone();
        }
        var ids = new JsonObject();
        foreach (var (k, v) in chunk) ids[k] = v.DeepClone();
        // данные дашборда приоритетнее подробностей (как в оригинале)
        return J.Merge(ids, fetched);
    }

    private async Task FetchInventoryAsync()
    {
        Ui.SetStatus(L.T("gui.status.fetching_inventory", "Fetching inventory..."));
        var response = await GqlAsync(Gql.Inventory());
        var inventory = response.At("data", "currentUser", "inventory");
        var claimedBenefits = new Dictionary<string, DateTimeOffset>();
        foreach (var b in inventory.Arr("gameEventDrops"))
            if (b.Str("id") is { } bid) claimedBenefits[bid] = b.Time("lastAwardedAt");
        var inventoryData = new JsonObject();
        foreach (var c in inventory.Arr("dropCampaignsInProgress"))
            if (c.Str("id") is { } cid) inventoryData[cid] = c.DeepClone();

        response = await GqlAsync(Gql.Campaigns());
        var available = new List<KeyValuePair<string, JsonObject>>();
        foreach (var c in response.Arr("data", "currentUser", "dropCampaigns"))
            if (c.Str("status") is "ACTIVE" or "UPCOMING" && c.Str("id") is { } cid && c is JsonObject obj)
                available.Add(new(cid, obj));

        Ui.SetStatus(L.T("gui.status.fetching_campaigns", "Fetching campaigns..."));
        var chunks = await Task.WhenAll(available.Chunk(20).Select(ch => FetchCampaignsAsync(ch.ToList())));
        // данные инвентаря (с прогрессом) приоритетнее общих данных кампаний
        foreach (var chunk in chunks) inventoryData = J.Merge(inventoryData, chunk);
        if (available.Count == 0 && Settings.UseCampaignCatalog)
        {
            // Twitch не отдал список кампаний этому клиенту (integrity-проверка) — берём публичный каталог
            var catalog = await CampaignCatalog.FetchAsync(Settings, Token);
            if (catalog.Count > 0)
            {
                Log.Info($"Twitch returned no campaign list, using the public catalog ({catalog.Count} campaigns)");
                inventoryData = J.Merge(inventoryData, catalog);
            }
            else
            {
                Print(L.T("x.catalog_failed", "Twitch did not return the campaign list and the public catalog is unavailable."));
            }
        }
        // отбрасываем кампании без игры
        foreach (var key in inventoryData.Where(kv => kv.Value.IsNull("game")).Select(kv => kv.Key).ToList())
            inventoryData.Remove(key);

        if (Settings.ArgDump) WriteDump(inventoryData, inventory);

        var campaigns = new List<DropsCampaign>();
        foreach (var (_, data) in inventoryData)
        {
            try { campaigns.Add(new DropsCampaign(this, data!, claimedBenefits)); }
            catch (Exception ex) { Log.Warning($"Skipping campaign {data.Str("id")}: {ex.Message}"); }
        }
        campaigns = campaigns
            .OrderByDescending(c => c.Eligible)
            .ThenBy(c => c.Upcoming ? c.StartsAt : c.EndsAt)
            .ThenByDescending(c => c.Active)
            .ToList();

        _drops.Clear();
        Ui.InventoryClear();
        Inventory.Clear();
        _mntTriggers.Clear();
        var triggers = new HashSet<DateTimeOffset>();
        var nextHour = DateTimeOffset.UtcNow.AddHours(1);
        foreach (var campaign in campaigns)
        {
            foreach (var d in campaign.Drops) _drops[d.Id] = d;
            if (campaign.CanEarnWithin(nextHour)) triggers.UnionWith(campaign.TimeTriggers);
            Inventory.Add(campaign);
            CampaignsById[campaign.Id] = campaign;
        }
        int i = 0;
        foreach (var campaign in campaigns)
        {
            await Ui.InventoryAddCampaignAsync(campaign);
            i++;
            Ui.SetStatus(L.F("gui.status.adding_campaigns", "Adding campaigns to inventory... {counter}", ("counter", $"({i}/{campaigns.Count})")));
            Token.ThrowIfCancellationRequested();
        }
        var now = DateTimeOffset.UtcNow;
        _mntTriggers.AddRange(triggers.Where(t => t > now).OrderBy(t => t));
        // задача обслуживания перезапускается после каждой загрузки инвентаря
        _mntCts?.Cancel();
        _mntCts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _mntTask = MaintenanceAsync(_mntCts.Token);
    }

    private static void WriteDump(JsonObject inventoryData, JsonNode? inventory)
    {
        var dump = (JsonObject)inventoryData.DeepClone();
        foreach (var (_, c) in dump)
        {
            // списки ACL заменяем количеством, ID экземпляров дропов — многоточием (там есть user ID)
            if (c.At("allow", "channels") is JsonArray { Count: > 0 } acl && (c.At("allow", "isEnabled") is null || c.Bool("allow", "isEnabled")))
                c!["allow"]!["channels"] = $"{acl.Count} channels";
            foreach (var d in c.Arr("timeBasedDrops"))
                if (d.At("self") is JsonObject self && self.Str("dropInstanceID") is { Length: > 0 })
                    self["dropInstanceID"] = "...";
        }
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.AppendAllText(AppPaths.DumpFile, dump.ToJsonString(opts) + "\n\n" + (inventory.At("gameEventDrops")?.ToJsonString(opts) ?? "[]"));
    }

    private async Task<List<Channel>> GetLiveStreamsAsync(Game game, int limit = 20, bool dropsEnabled = true)
    {
        JsonNode response;
        try
        {
            response = await GqlAsync(Gql.GameDirectory(game.Slug, limit, dropsEnabled));
        }
        catch (GqlException ex)
        {
            throw new MinerException($"Game: {game.Slug}", ex);
        }
        return response.Arr("data", "game", "streams", "edges")
            .Select(e => e["node"])
            .Where(n => n is not null && !n.IsNull("broadcaster"))
            .Select(n => Channel.FromDirectory(this, n!, dropsEnabled))
            .ToList();
    }

    /// <summary>Пакетная проверка статуса множества каналов (и наличия дропов, если включено).</summary>
    private async Task BulkCheckOnlineAsync(IReadOnlyCollection<Channel> channels)
    {
        if (channels.Count == 0) return;
        var streams = new Dictionary<long, JsonNode>();
        var results = await Task.WhenAll(channels.Select(c => c.StreamGql).Chunk(20).Select(ch => GqlBatchAsync(ch)));
        foreach (var r in results.SelectMany(x => x))
            if (r.At("data", "user") is JsonNode user && user.Long("id") is var id and > 0)
                streams[id] = user;

        var availableDrops = new Dictionary<long, List<JsonNode>>();
        if (Settings.AvailableDropsCheck)
        {
            var ops = streams.Where(kv => !kv.Value.IsNull("stream")).Select(kv => Gql.AvailableDrops(kv.Key)).ToList();
            var avail = await Task.WhenAll(ops.Chunk(20).Select(ch => GqlBatchAsync(ch)));
            foreach (var r in avail.SelectMany(x => x))
            {
                var info = r.At("data", "channel");
                if (info is not null) availableDrops[info.Long("id")] = info.Arr("viewerDropCampaigns").ToList();
            }
        }
        foreach (var channel in channels)
        {
            if (!streams.TryGetValue(channel.Id, out var data) || data.IsNull("stream")) continue;
            channel.ExternalUpdate(data, availableDrops.GetValueOrDefault(channel.Id) ?? []);
        }
    }

    #endregion

    /// <summary>Импорт входа из cookies.jar Python-версии, затем перезапуск майнера.</summary>
    public async Task ImportLegacyLoginAsync(string path)
    {
        try
        {
            var result = await Auth.ImportLegacyAsync(path);
            Print(result);
            if (Auth.AccessToken is not null && Auth.UserId == 0) ChangeState(MinerState.Restart);
        }
        catch (Exception ex)
        {
            Print($"Import failed: {ex.Message}");
        }
    }

    /// <summary>Выход из аккаунта (кнопка на вкладке «Помощь»).</summary>
    public async Task LogoutAsync()
    {
        try
        {
            await Auth.RevokeAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Logout failed: {ex.Message}");
            Auth.Invalidate(deleteStored: true);
        }
        ChangeState(MinerState.Restart);
    }
}
