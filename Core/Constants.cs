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
    // Клиент Android TV (SmartBox). На октябрь 2026 Twitch отклоняет вход по коду для клиента
    // Android-приложения ("invalid client"), а SmartBox работает — поэтому он основной.
    public static readonly ClientInfo SmartBox = new(
        "https://android.tv.twitch.tv",
        "ue6666qo983tsx6so1t0vnawi233wa",
        "Mozilla/5.0 (Linux; Android 7.1; Smart Box C1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36");

    public static readonly ClientInfo MobileWeb = new(
        "https://m.twitch.tv",
        "r8s4dac0uhzifbpu9sjdiwzctle17ff",
        "Mozilla/5.0 (Linux; Android 16) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.7204.158 Mobile Safari/537.36",
        "Mozilla/5.0 (Linux; Android 16; SM-A205U) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.7204.158 Mobile Safari/537.36",
        "Mozilla/5.0 (Linux; Android 16; SM-G960U) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.7204.158 Mobile Safari/537.36");

    /// <summary>Веб-клиент twitch.tv — вход через встроенный браузер (WebView2).</summary>
    public static readonly ClientInfo Web = new(
        "https://www.twitch.tv",
        "kimne78kx3ncx6brgo4mv6wki5h1ko",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36");

    /// <summary>Порядок перебора клиентов при входе по коду устройства.</summary>
    public static ClientInfo[] Candidates => [SmartBox, MobileWeb, AndroidApp];

    /// <summary>Все известные клиенты (для сохранённых токенов).</summary>
    public static ClientInfo[] Known => [SmartBox, MobileWeb, AndroidApp, Web];

    public static ClientInfo? ById(string? clientId) => Known.FirstOrDefault(c => c.ClientId == clientId);

    public static bool IsWeb(ClientInfo c) => c.ClientId == Web.ClientId;

    // Клиент Android-приложения Twitch (использовался в оригинале).
    public static readonly ClientInfo AndroidApp = new(
        "https://www.twitch.tv",
        "kd1unb4b3q4t58fwlpcbzcbnm76a8fp",
        "Dalvik/2.1.0 (Linux; U; Android 16; SM-S911B Build/TP1A.220624.014) tv.twitch.android.app/25.3.0/2503006",
        "Dalvik/2.1.0 (Linux; U; Android 16; SM-S938B Build/BP2A.250605.031) tv.twitch.android.app/25.3.0/2503006",
        "Dalvik/2.1.0 (Linux; Android 16; SM-X716N Build/UP1A.231005.007) tv.twitch.android.app/25.3.0/2503006",
        "Dalvik/2.1.0 (Linux; U; Android 15; SM-G990B Build/AP3A.240905.015.A2) tv.twitch.android.app/25.3.0/2503006",
        "Dalvik/2.1.0 (Linux; U; Android 15; SM-G970F Build/AP3A.241105.008) tv.twitch.android.app/25.3.0/2503006",
        "Dalvik/2.1.0 (Linux; U; Android 15; SM-A566E Build/AP3A.240905.015.A2) tv.twitch.android.app/25.3.0/2503006",
        "Dalvik/2.1.0 (Linux; U; Android 14; SM-X306B Build/UP1A.231005.007) tv.twitch.android.app/25.3.0/2503006");
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
