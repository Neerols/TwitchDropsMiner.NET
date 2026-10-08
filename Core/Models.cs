using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TwitchDropsMiner.Core;

public sealed partial class Game : IEquatable<Game>
{
    private static readonly HashSet<long> SpecialGameIds = [509663, 509672];

    public long Id { get; }
    public string Name { get; }
    public string Slug { get; }

    public Game(JsonNode data)
    {
        Id = data.Long("id");
        Name = data.Str("displayName") is { Length: > 0 } dn ? dn : data.Str("name") ?? "";
        Slug = data.Str("slug") is { Length: > 0 } s ? s : MakeSlug(Name);
    }

    /// <summary>Игры, по которым дропы можно получать на любом канале.</summary>
    public bool IsSpecial => SpecialGameIds.Contains(Id);

    private static string MakeSlug(string name)
    {
        var slug = name.ToLowerInvariant().Replace("'", "");
        slug = NonWord().Replace(slug, "-");
        slug = MultiDash().Replace(slug.Trim('-'), "-");
        return slug;
    }

    [GeneratedRegex(@"\W+")] private static partial Regex NonWord();
    [GeneratedRegex(@"-{2,}")] private static partial Regex MultiDash();

    public bool Equals(Game? other) => other is not null && other.Id == Id;
    public override bool Equals(object? obj) => Equals(obj as Game);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => Name;
}

public sealed class LiveStream
{
    private readonly Channel _channel;
    private string? _spadePayload;

    public long BroadcastId { get; }
    public int Viewers { get; set; }
    public bool DropsEnabled { get; set; }
    public Game? Game { get; }
    public string Title { get; }

    public LiveStream(Channel channel, long id, JsonNode? game, int viewers, string title)
    {
        _channel = channel;
        BroadcastId = id;
        Viewers = viewers;
        // Если проверка доступных дропов выключена — считаем, что дропы на стриме есть
        DropsEnabled = !channel.Miner.Settings.AvailableDropsCheck;
        Game = game is not null && game.Has("id") ? new Game(game) : null;
        Title = title;
    }

    public static LiveStream FromGetStream(Channel channel, JsonNode channelData) => new(
        channel,
        channelData.Long("stream", "id"),
        channelData.At("broadcastSettings", "game"),
        channelData.Int("stream", "viewersCount"),
        channelData.Str("broadcastSettings", "title") ?? "");

    public static LiveStream FromDirectory(Channel channel, JsonNode data, bool dropsEnabled) => new(
        channel, data.Long("id"), data.At("game"), data.Int("viewersCount"), data.Str("title") ?? "")
    {
        DropsEnabled = dropsEnabled,
    };

    /// <summary>Событие «minute-watched» для spade (base64 от минифицированного JSON).</summary>
    public string SpadePayload => _spadePayload ??= BuildSpadePayload();

    private string BuildSpadePayload()
    {
        var payload = new JsonArray(new JsonObject
        {
            ["event"] = "minute-watched",
            ["properties"] = new JsonObject
            {
                ["broadcast_id"] = BroadcastId.ToString(),
                ["channel_id"] = _channel.Id.ToString(),
                ["channel"] = _channel.Login,
                ["client_time"] = Util.IsoNow(),
                ["game"] = Game?.Name ?? "",
                ["game_id"] = Game?.Id.ToString() ?? "",
                ["hidden"] = false,
                ["is_live"] = true,
                ["live"] = true,
                ["logged_in"] = true,
                ["minutes_logged"] = 1,
                ["muted"] = false,
                ["user_id"] = _channel.Miner.Auth.UserId,
            },
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(J.Minify(payload)));
    }
}

public sealed partial class Channel : IEquatable<Channel>
{
    public Miner Miner { get; }
    public long Id { get; }
    public string Login { get; }
    private string? _displayName;
    private string? _spadeUrl;
    private LiveStream? _stream;
    private CancellationTokenSource? _pendingStreamUp;

    /// <summary>
    /// Каналы из списка разрешённых кампании (ACL): проверяются первыми при выборе,
    /// не удаляются при очистке, пока транслируют нужную игру.
    /// </summary>
    public bool AclBased { get; }

    public Channel(Miner miner, long id, string login, string? displayName = null, bool aclBased = false)
    {
        Miner = miner;
        Id = id;
        Login = login;
        _displayName = displayName;
        AclBased = aclBased;
    }

    public static Channel FromAcl(Miner miner, JsonNode data) =>
        new(miner, data.Long("id"), data.Str("name") ?? "", data.Str("displayName"), aclBased: true);

    public static Channel FromDirectory(Miner miner, JsonNode data, bool dropsEnabled)
    {
        var b = data["broadcaster"]!;
        var ch = new Channel(miner, b.Long("id"), b.Str("login") ?? "", b.Str("displayName"));
        ch._stream = LiveStream.FromDirectory(ch, data, dropsEnabled);
        return ch;
    }

    public string Name => _displayName ?? Login;
    public string Url => $"{Miner.Client.ClientUrl.ToString().TrimEnd('/')}/{Login}";
    public bool Online => _stream is not null;
    public bool Offline => _stream is null && _pendingStreamUp is null;
    public bool PendingOnline => _stream is null && _pendingStreamUp is not null;
    public Game? Game => _stream?.Game;
    public int? Viewers
    {
        get => _stream?.Viewers;
        set { if (_stream is not null && value is int v) _stream.Viewers = v; }
    }
    public bool DropsEnabled => _stream?.DropsEnabled ?? false;
    public JsonObject StreamGql => Gql.GetStreamInfo(Login);

    public void Display(bool add = false) => Miner.Ui.ChannelDisplay(this, add);

    public void Remove()
    {
        CancelPending();
        Miner.Ui.ChannelRemove(this);
    }

    private void CancelPending()
    {
        if (_pendingStreamUp is null) return;
        _pendingStreamUp.Cancel();
        _pendingStreamUp.Dispose();
        _pendingStreamUp = null;
    }

    [GeneratedRegex(@"src=""(https://[\w.]+/config/settings\.[0-9a-f]{32}\.js)""", RegexOptions.IgnoreCase)]
    private static partial Regex SettingsPattern();
    // URL может содержать query-строку (у SmartBox: https://spade.twitch.tv/track?allow_stream=true)
    [GeneratedRegex(@"""spade_?url"": ?""(https://[^""\s]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SpadePattern();

    private const string FallbackSpadeUrl = "https://spade.twitch.tv/track";

    /// <summary>
    /// URL для отправки событий просмотра: страница стримера (HTML) → settings.js → spade_url.
    /// Пробуем страницу клиента и www.twitch.tv по одному разу (страница SmartBox иногда отвечает 555);
    /// если ничего не нашли — используем стандартный адрес spade.
    /// </summary>
    private async Task<string> GetSpadeUrlAsync()
    {
        foreach (var pageUrl in new[] { Url, $"https://www.twitch.tv/{Login}" }.Distinct())
        {
            var html = await Miner.Http.TryGetStringOnceAsync(pageUrl);
            if (html is null) continue;
            var match = SpadePattern().Match(html);
            if (match.Success) return match.Groups[1].Value;
            var settings = SettingsPattern().Match(html);
            if (!settings.Success) continue;
            var js = await Miner.Http.TryGetStringOnceAsync(settings.Groups[1].Value);
            if (js is not null && SpadePattern().Match(js) is { Success: true } m) return m.Groups[1].Value;
        }
        Log.Warning("spade_url extraction failed, using the fallback URL");
        return FallbackSpadeUrl;
    }

    private bool CheckDropsEnabled(IEnumerable<JsonNode> availableDrops) =>
        availableDrops.Any(c => Miner.CampaignsById.TryGetValue(c.Str("id") ?? "", out var campaign)
                                && campaign.CanEarn(this, ignoreChannelStatus: true));

    /// <summary>Обновление стрима по данным массовой проверки (при перезагрузке).</summary>
    public void ExternalUpdate(JsonNode channelData, IEnumerable<JsonNode> availableDrops)
    {
        _displayName ??= channelData.Str("displayName");
        if (channelData.IsNull("stream")) { _stream = null; return; }
        var stream = LiveStream.FromGetStream(this, channelData);
        if (!stream.DropsEnabled) stream.DropsEnabled = CheckDropsEnabled(availableDrops);
        _stream = stream;
    }

    private async Task<LiveStream?> GetStreamAsync()
    {
        JsonNode response;
        try
        {
            response = await Miner.GqlAsync(StreamGql);
        }
        catch (MinerException ex)
        {
            throw new MinerException($"Channel: {Login}", ex);
        }
        var data = response.At("data", "user");
        if (data is null) return null;
        _displayName ??= data.Str("displayName");
        if (data.IsNull("stream")) return null;
        var stream = LiveStream.FromGetStream(this, data);
        if (!stream.DropsEnabled)
        {
            try
            {
                var avail = await Miner.GqlAsync(Gql.AvailableDrops(Id));
                stream.DropsEnabled = CheckDropsEnabled(avail.Arr("data", "channel", "viewerDropCampaigns"));
            }
            catch (MinerException)
            {
                Log.Call($"AvailableDrops GQL call failed for channel: {Login}");
            }
        }
        return stream;
    }

    public async Task<bool> UpdateStreamAsync()
    {
        var old = _stream;
        _stream = await GetStreamAsync();
        Miner.OnChannelUpdate(this, old, _stream);
        return _stream is not null;
    }

    /// <summary>
    /// Событие stream-up приходит раньше, чем стрим реально доступен —
    /// ждём ONLINE_DELAY и перепроверяем состояние.
    /// </summary>
    public void CheckOnline()
    {
        if (_pendingStreamUp is not null) return;
        var cts = new CancellationTokenSource();
        _pendingStreamUp = cts;
        Display();
        _ = OnlineDelayAsync(cts);
    }

    private async Task OnlineDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Limits.OnlineDelay, cts.Token);
            if (_pendingStreamUp == cts) { _pendingStreamUp = null; cts.Dispose(); }
            await UpdateStreamAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Ошибка проверки статуса канала {Login}: {ex.Message}");
        }
    }

    public void SetOffline()
    {
        bool needsDisplay = false;
        if (_pendingStreamUp is not null)
        {
            CancelPending();
            needsDisplay = true;
        }
        if (Online)
        {
            var old = _stream;
            _stream = null;
            Miner.OnChannelUpdate(this, old, null);  // сам вызывает Display()
            needsDisplay = false;
        }
        if (needsDisplay) Display();
    }

    private string? _playlistUrl;

    /// <summary>
    /// «Просмотр» через HLS: берём плейлист самого низкого качества и делаем HEAD последнего сегмента.
    /// Видео не скачивается. В оригинале этот способ есть, но отключён (_send_watch_playlist).
    /// </summary>
    public async Task<bool> SendPlaylistWatchAsync()
    {
        if (_stream is null) return false;
        try
        {
            if (_playlistUrl is null)
            {
                var tokenResp = await Miner.GqlAsync(Gql.PlaybackAccessToken(Login));
                var value = tokenResp.Str("data", "streamPlaybackAccessToken", "value");
                var sig = tokenResp.Str("data", "streamPlaybackAccessToken", "signature");
                if (value is null || sig is null) return false;
                var master = await Miner.Http.TryGetStringOnceAsync(
                    $"https://usher.ttvnw.net/api/channel/hls/{Login}.m3u8?sig={sig}&token={Uri.EscapeDataString(value)}&allow_source=true&player_backend=mediaplayer");
                var last = master?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("https://"));
                if (last is null) return false;
                _playlistUrl = last;
            }
            var playlist = await Miner.Http.TryGetStringOnceAsync(_playlistUrl);
            if (playlist is null) { _playlistUrl = null; return false; }
            var segment = playlist.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("https://"));
            if (segment is null) return false;
            var head = await Miner.Http.RequestAsync(HttpMethod.Head, segment);
            return head.Status == 200;
        }
        catch (MinerException ex)
        {
            Log.Call($"Playlist watch failed for {Login}: {ex.Message}");
            _playlistUrl = null;
            return false;
        }
    }

    /// <summary>Отправка события «минута просмотра» (spade). true — если Twitch ответил 204.</summary>
    public async Task<bool> SendWatchAsync()
    {
        if (_stream is null) return false;
        _spadeUrl ??= await GetSpadeUrlAsync();
        try
        {
            var payload = _stream.SpadePayload;
            var status = await Miner.Http.PostStatusAsync(_spadeUrl, () => new FormUrlEncodedContent([new("data", payload)]));
            return status == 204;
        }
        catch (MinerException)
        {
            return false;
        }
    }

    public bool Equals(Channel? other) => other is not null && other.Id == Id;
    public override bool Equals(object? obj) => Equals(obj as Channel);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => $"Channel({Name}, {Id})";
}

public enum BenefitType { Unknown, Badge, Emote, DirectEntitlement }

public sealed class Benefit
{
    public string Id { get; }
    public string Name { get; }
    public BenefitType Type { get; }
    public string ImageUrl { get; }

    public Benefit(JsonNode data)
    {
        var b = data["benefit"]!;
        Id = b.Str("id") ?? "";
        Name = b.Str("name") ?? "";
        Type = b.Str("distributionType") switch
        {
            "BADGE" => BenefitType.Badge,
            "EMOTE" => BenefitType.Emote,
            "DIRECT_ENTITLEMENT" => BenefitType.DirectEntitlement,
            _ => BenefitType.Unknown,
        };
        ImageUrl = b.Str("imageAssetURL") ?? "";
    }

    public bool IsBadgeOrEmote => Type is BenefitType.Badge or BenefitType.Emote;
}

public sealed class TimedDrop
{
    // Twitch иногда ошибочно отдаёт эту дату как время последнего получения
    private static readonly DateTimeOffset BuggedTime = new(1, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Miner _miner;
    public string Id { get; }
    public string Name { get; }
    public DropsCampaign Campaign { get; }
    public List<Benefit> Benefits { get; }
    public DateTimeOffset StartsAt { get; }
    public DateTimeOffset EndsAt { get; }
    public string? ClaimId { get; set; }
    public bool IsClaimed { get; private set; }
    public List<string> PreconditionDrops { get; }
    public int RealCurrentMinutes { get; private set; }
    public int ExtraCurrentMinutes { get; private set; }
    public int RequiredMinutes { get; }

    public TimedDrop(DropsCampaign campaign, JsonNode data, Dictionary<string, DateTimeOffset> claimedBenefits)
    {
        _miner = campaign.Miner;
        Campaign = campaign;
        Id = data.Str("id") ?? "";
        Name = data.Str("name") ?? "";
        Benefits = data.Arr("benefitEdges").Select(b => new Benefit(b)).ToList();
        StartsAt = data.Time("startAt");
        EndsAt = data.Time("endAt");
        if (data.At("self") is JsonObject self)
        {
            ClaimId = self.Str("dropInstanceID");
            IsClaimed = self.Bool("isClaimed");
        }
        else
        {
            // Без self определяем «получен ли дроп» по времени получения его наград
            var dts = Benefits.Where(b => claimedBenefits.ContainsKey(b.Id)).Select(b => claimedBenefits[b.Id]).ToList();
            if (dts.Count > 0 && dts.All(dt => (StartsAt <= dt && dt < EndsAt) || dt == BuggedTime))
                IsClaimed = true;
        }
        PreconditionDrops = data.Arr("preconditionDrops").Select(d => d.Str("id") ?? "").ToList();
        RealCurrentMinutes = data.Int("self", "currentMinutesWatched");
        RequiredMinutes = data.Int("requiredMinutesWatched");
        if (IsClaimed) RealCurrentMinutes = RequiredMinutes;
    }

    public int CurrentMinutes => RealCurrentMinutes + ExtraCurrentMinutes;
    public int RemainingMinutes => RequiredMinutes - CurrentMinutes;

    public int TotalRequiredMinutes => RequiredMinutes + PreconditionDrops
        .Select(pid => Campaign.TimedDrops.TryGetValue(pid, out var d) ? d.TotalRequiredMinutes : 0)
        .DefaultIfEmpty(0).Max();

    public int TotalRemainingMinutes => RemainingMinutes + PreconditionDrops
        .Select(pid => Campaign.TimedDrops.TryGetValue(pid, out var d) ? d.TotalRemainingMinutes : 0)
        .DefaultIfEmpty(0).Max();

    public double Progress =>
        CurrentMinutes <= 0 || RequiredMinutes <= 0 ? 0.0
        : CurrentMinutes >= RequiredMinutes ? 1.0
        : (double)CurrentMinutes / RequiredMinutes;

    public double Availability
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            if (RequiredMinutes > 0 && TotalRemainingMinutes > 0 && now < EndsAt)
                return (EndsAt - now).TotalMinutes / TotalRemainingMinutes;
            return double.PositiveInfinity;
        }
    }

    public bool PreconditionsMet =>
        PreconditionDrops.All(pid => Campaign.TimedDrops.TryGetValue(pid, out var d) && d.IsClaimed);

    private bool BaseEarnConditions() =>
        PreconditionsMet
        && !IsClaimed
        && (Benefits.Count > 0 || Campaign.PreconditionsChain().Contains(Id))
        && RequiredMinutes > 0
        // Если оценочных минут слишком много — значит прогресс не идёт, ждём перезагрузки
        && ExtraCurrentMinutes < Limits.MaxExtraMinutes;

    internal bool BaseCanEarn()
    {
        var now = DateTimeOffset.UtcNow;
        return BaseEarnConditions() && StartsAt <= now && now < EndsAt;
    }

    internal bool CanEarnWithin(DateTimeOffset stamp) =>
        BaseEarnConditions() && EndsAt > DateTimeOffset.UtcNow && StartsAt < stamp;

    public bool CanEarn(Channel? channel = null, bool ignoreChannelStatus = false) =>
        BaseCanEarn() && Campaign.BaseCanEarn(channel, ignoreChannelStatus);

    /// <summary>Получить награду можно до 24 часов после окончания кампании.</summary>
    public bool CanClaim =>
        ClaimId is not null && !IsClaimed && DateTimeOffset.UtcNow < Campaign.EndsAt.AddHours(24);

    public string RewardsText(string delim = ", ") => string.Join(delim, Benefits.Select(b => b.Name));

    private void OnStateChanged() => _miner.Ui.InventoryUpdateDrop(this);

    internal void UpdateRealMinutes(int delta)
    {
        if (delta == 0 || RealCurrentMinutes + delta < 0) return;
        RealCurrentMinutes = RealCurrentMinutes + delta < RequiredMinutes ? RealCurrentMinutes + delta : RequiredMinutes;
        ExtraCurrentMinutes = 0;
        OnStateChanged();
    }

    internal bool BumpMinutes(Channel? channel)
    {
        if (!CanEarn(channel)) return false;
        ExtraCurrentMinutes++;
        OnStateChanged();
        return ExtraCurrentMinutes >= Limits.MaxExtraMinutes;
    }

    public void UpdateMinutes(int newMinutes)
    {
        int delta = newMinutes - RealCurrentMinutes;
        if (delta == 0) return;
        if (RealCurrentMinutes + delta < 0) delta = -RealCurrentMinutes;
        else if (RealCurrentMinutes + delta > RequiredMinutes) delta = RequiredMinutes - RealCurrentMinutes;
        Campaign.UpdateRealMinutes(delta);
    }

    public void Display(bool countdown = true, bool subOne = false) => _miner.DisplayDrop(this, countdown, subOne);

    public async Task<bool> ClaimAsync()
    {
        bool result = await ClaimInternalAsync();
        if (result)
        {
            IsClaimed = true;
            RealCurrentMinutes = RequiredMinutes;
            ExtraCurrentMinutes = 0;
            var text = $"{Campaign.Game.Name}\n{RewardsText()} ({Campaign.ClaimedDrops}/{Campaign.TotalDrops})";
            _miner.Print(L.F("status.claimed_drop", "Claimed drop: {drop}", ("drop", text.Replace('\n', ' '))));
            if (_miner.Settings.TrayNotifications)
                _miner.Ui.Notify(L.T("gui.tray.notification_title", "Mined Drop"), text);
        }
        else
        {
            Log.Error($"Drop claim has potentially failed! Drop ID: {Id}");
        }
        OnStateChanged();
        return result;
    }

    private async Task<bool> ClaimInternalAsync()
    {
        if (IsClaimed) return true;
        if (!CanClaim) return false;
        JsonNode response;
        try
        {
            response = await _miner.GqlAsync(Gql.ClaimDrop(ClaimId!));
        }
        catch (MinerException)
        {
            // при любой ошибке считаем, что получение могло не пройти
            return false;
        }
        var data = response["data"];
        if (data.At("errors") is JsonArray { Count: > 0 }) return false;
        var status = data.Str("claimDropRewards", "status");
        return status is "ELIGIBLE_FOR_ALL" or "DROP_INSTANCE_ALREADY_CLAIMED";
    }

    public override string ToString() => $"Drop({RewardsText()}, {CurrentMinutes}/{RequiredMinutes})";
}

public sealed partial class DropsCampaign
{
    public Miner Miner { get; }
    public string Id { get; }
    public string Name { get; }
    public Game Game { get; }
    public bool Linked { get; }
    public string LinkUrl { get; }
    public string ImageUrl { get; }
    public DateTimeOffset StartsAt { get; }
    public DateTimeOffset EndsAt { get; }
    private readonly bool _valid;
    public List<Channel> AllowedChannels { get; }
    public Dictionary<string, TimedDrop> TimedDrops { get; }

    [GeneratedRegex(@"-\d+x\d+(?=\.(?:jpg|png|gif)$)", RegexOptions.IgnoreCase)]
    private static partial Regex DimsPattern();

    public DropsCampaign(Miner miner, JsonNode data, Dictionary<string, DateTimeOffset> claimedBenefits)
    {
        Miner = miner;
        Id = data.Str("id") ?? "";
        Name = data.Str("name") ?? "";
        Game = new Game(data["game"]!);
        Linked = data.Bool("self", "isAccountConnected");
        LinkUrl = data.Str("accountLinkURL") ?? "https://www.twitch.tv/drops/campaigns";
        // картинка кампании — обложка игры без размеров в имени (".../game-285x380.jpg")
        ImageUrl = DimsPattern().Replace(data.Str("game", "boxArtURL") ?? "", "");
        StartsAt = data.Time("startAt");
        EndsAt = data.Time("endAt");
        _valid = data.Str("status") != "EXPIRED";
        var allow = data.At("allow");
        bool aclEnabled = allow.At("isEnabled") is null || allow.Bool("isEnabled");
        AllowedChannels = aclEnabled ? allow.Arr("channels").Select(c => Channel.FromAcl(miner, c)).ToList() : [];
        TimedDrops = new Dictionary<string, TimedDrop>();
        foreach (var d in data.Arr("timeBasedDrops"))
        {
            var drop = new TimedDrop(this, d, claimedBenefits);
            TimedDrops[drop.Id] = drop;
        }
        HasBadgeOrEmote = Drops.Any(d => d.Benefits.Any(b => b.IsBadgeOrEmote));
    }

    public IEnumerable<TimedDrop> Drops => TimedDrops.Values;
    public bool HasBadgeOrEmote { get; }

    public IEnumerable<DateTimeOffset> TimeTriggers =>
        new[] { StartsAt, EndsAt }.Concat(Drops.SelectMany(d => new[] { d.StartsAt, d.EndsAt })).Distinct();

    public bool Active { get { var now = DateTimeOffset.UtcNow; return _valid && StartsAt <= now && now < EndsAt; } }
    public bool Upcoming => _valid && DateTimeOffset.UtcNow < StartsAt;
    public bool Expired => !_valid || EndsAt <= DateTimeOffset.UtcNow;
    public int TotalDrops => TimedDrops.Count;
    public bool Eligible => HasBadgeOrEmote ? Miner.Settings.EnableBadgesEmotes : Linked;
    public bool Finished => Drops.All(d => d.IsClaimed || d.RequiredMinutes <= 0);
    public int ClaimedDrops => Drops.Count(d => d.IsClaimed);
    public int RequiredMinutes => Drops.Select(d => d.TotalRequiredMinutes).DefaultIfEmpty(0).Max();
    public int RemainingMinutes => Drops.Select(d => d.TotalRemainingMinutes).DefaultIfEmpty(0).Max();
    public double Progress => TotalDrops == 0 ? 0 : Drops.Sum(d => d.Progress) / TotalDrops;
    public double Availability => Drops.Select(d => d.Availability).DefaultIfEmpty(double.PositiveInfinity).Min();

    public TimedDrop? FirstDrop =>
        Drops.Where(d => d.CanEarn()).OrderBy(d => d.RemainingMinutes).FirstOrDefault();

    internal void UpdateRealMinutes(int delta)
    {
        foreach (var d in Drops) d.UpdateRealMinutes(delta);
        FirstDrop?.Display();
    }

    internal bool BaseCanEarn(Channel? channel, bool ignoreChannelStatus) =>
        Eligible
        && Active
        && (channel is null
            || ((AllowedChannels.Count == 0 || AllowedChannels.Contains(channel))
                && (ignoreChannelStatus
                    || (channel.Game is not null && channel.Game.Equals(Game))
                    || Game.IsSpecial)));

    public HashSet<string> PreconditionsChain() =>
        Drops.Where(d => !d.IsClaimed).SelectMany(d => d.PreconditionDrops).ToHashSet();

    /// <summary>true, если хотя бы один дроп кампании можно получать прямо сейчас.</summary>
    public bool CanEarn(Channel? channel = null, bool ignoreChannelStatus = false) =>
        BaseCanEarn(channel, ignoreChannelStatus) && Drops.Any(d => d.BaseCanEarn());

    /// <summary>Как CanEarn, но без канала и с прицелом на будущее время.</summary>
    public bool CanEarnWithin(DateTimeOffset stamp) =>
        Eligible && _valid && EndsAt > DateTimeOffset.UtcNow && StartsAt < stamp
        && Drops.Any(d => d.CanEarnWithin(stamp));

    public void BumpMinutes(Channel channel)
    {
        var results = Drops.Select(d => d.BumpMinutes(channel)).ToList();  // все дропы, без short-circuit
        if (results.Any(r => r))
        {
            Log.Warning($"At least one of the drops in campaign \"{Name}({Game.Name})\" has reached the maximum extra minutes limit!");
            Miner.ChangeState(MinerState.ChannelSwitch);
        }
        FirstDrop?.Display();
    }

    public override string ToString() => $"Campaign({Game}, {Name}, {ClaimedDrops}/{TotalDrops})";
}
