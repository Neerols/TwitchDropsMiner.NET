namespace TwitchDropsMiner.Core;

/// <summary>Данные клиента Twitch, от имени которого работает приложение.</summary>
public sealed class ClientInfo
{
    public Uri ClientUrl { get; }
    public string ClientId { get; }
    public string UserAgent { get; }

    public ClientInfo(string clientUrl, string clientId, params string[] userAgents)
    {
        ClientUrl = new Uri(clientUrl);
        ClientId = clientId;
        UserAgent = userAgents[Random.Shared.Next(userAgents.Length)];
    }

    /// <summary>Тот же клиент с другим User-Agent (для веб-входа берём UA встроенного браузера).</summary>
    public ClientInfo WithUserAgent(string? userAgent) =>
        string.IsNullOrWhiteSpace(userAgent) ? this : new ClientInfo(ClientUrl.ToString(), ClientId, userAgent);
}

public static class ClientType
{
    /// <summary>
    /// Веб-клиент twitch.tv. Вход — через встроенный браузер (WebView2). С осени 2026 Twitch засчитывает
    /// просмотр только через настоящий плеер, а вход по коду для других клиентов закрыт.
    /// </summary>
    public static readonly ClientInfo Web = new(
        "https://www.twitch.tv",
        "kimne78kx3ncx6brgo4mv6wki5h1ko",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36");

    public static ClientInfo? ById(string? clientId) => clientId == Web.ClientId ? Web : null;

    public static bool IsWeb(ClientInfo? c) => c?.ClientId == Web.ClientId;
}
public enum MinerState
{
    Idle,
    InventoryFetch,
    GamesUpdate,
    ChannelsFetch,
    ChannelsCleanup,
    ChannelSwitch,
    Restart,
    Exit,
}

public enum PriorityMode
{
    PriorityOnly = 0,
    EndingSoonest = 1,
    LowAvailabilityFirst = 2,
}

public enum TrayIconState { Pickaxe, Active, Idle, Error, Maint }

public static class Limits
{
    public const int MaxExtraMinutes = 15;
    public const int BaseTopics = 2;
    public const int MaxWebsockets = 8;
    public const int WsTopicsLimit = 50;
    public const int TopicsPerChannel = 2;
    public const int MaxTopics = MaxWebsockets * WsTopicsLimit - BaseTopics;
    public const int MaxChannels = MaxTopics / TopicsPerChannel;

    public static readonly TimeSpan PingInterval = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan OnlineDelay = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(59);
}

public static class Topics
{
    // User-топики (по user_id)
    public const string UserDrops = "user-drop-events";
    public const string UserNotifications = "onsite-notifications";
    // Channel-топики (по channel_id)
    public const string ChannelStreamState = "video-playback-by-id";
    public const string ChannelStreamUpdate = "broadcast-settings-update";

    public static string Make(string name, long targetId) => $"{name}.{targetId}";
}
