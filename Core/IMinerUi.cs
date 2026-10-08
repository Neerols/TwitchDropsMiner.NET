namespace TwitchDropsMiner.Core;

/// <summary>
/// Всё, что ядру нужно от интерфейса. Все методы вызываются в UI-потоке.
/// </summary>
public interface IMinerUi
{
    void Print(string message);
    void SetStatus(string text);

    void SetWebsocketStatus(int index, string? status, int? topics);
    void RemoveWebsocket(int index);

    void SetLoginStatus(string status, long? userId);
    /// <summary>Показать код активации устройства (null — скрыть).</summary>
    void ShowDeviceCode(string? userCode, string? verificationUrl);
    void SetLogoutEnabled(bool enabled);
    void GrabAttention();

    void ChannelsClear();
    void ChannelDisplay(Channel channel, bool add);
    void ChannelRemove(Channel channel);
    void SetWatching(Channel? channel);
    long? SelectedChannelId { get; }

    void DisplayDrop(TimedDrop? drop);

    void InventoryClear();
    Task InventoryAddCampaignAsync(DropsCampaign campaign);
    void InventoryUpdateDrop(TimedDrop drop);
    void SetGames(IEnumerable<Game> games);

    /// <summary>Integrity-токен веб-клиента из встроенного браузера (null — не удалось).</summary>
    Task<string?> GetIntegrityTokenAsync(CancellationToken ct);

    /// <summary>Смотреть канал настоящим плеером во встроенном браузере (null — остановить).</summary>
    void BrowserWatch(string? login);
    Task<string?> BrowserPlayerStateAsync();

    /// <summary>Инвентарь, который сайт Twitch получает на странице drops/inventory (реальный прогресс).</summary>
    Task<System.Text.Json.Nodes.JsonNode?> FetchSiteInventoryAsync(CancellationToken ct);

    void SetTrayIcon(TrayIconState state);
    void Notify(string title, string message);
}

/// <summary>
/// Обратный отсчёт текущей минуты просмотра (в оригинале — CampaignProgress._timer_loop).
/// Используется ядром, чтобы понять, что Twitch перестал присылать прогресс через websocket.
/// </summary>
public sealed class ProgressTracker
{
    public const int AlmostDoneSeconds = 10;

    private DateTime? _countdownStart;
    private int _seconds;

    public TimedDrop? Drop { get; private set; }

    public int Seconds
    {
        get
        {
            if (_countdownStart is null) return _seconds;
            int left = 60 - (int)(DateTime.UtcNow - _countdownStart.Value).TotalSeconds;
            if (left <= 0) { _countdownStart = null; _seconds = 0; return 0; }
            return left;
        }
    }

    public bool TimerRunning => _countdownStart is not null && Seconds > 0;

    public bool MinuteAlmostDone() => !TimerRunning || Seconds <= AlmostDoneSeconds;

    public void Stop()
    {
        if (_countdownStart is not null)
        {
            _seconds = Seconds;
            _countdownStart = null;
        }
    }

    /// <summary>Возвращает true, если стоит вывести строку о прогрессе в журнал.</summary>
    public bool Display(TimedDrop? drop, bool countdown, bool subOne)
    {
        Drop = drop;
        Stop();
        if (drop is null) { _seconds = 0; return false; }
        if (countdown)
        {
            if (drop.RemainingMinutes <= 0) _seconds = 60;
            else { _countdownStart = DateTime.UtcNow; _seconds = 60; }
            return true;
        }
        _seconds = subOne ? 0 : 60;
        return false;
    }

    /// <summary>Строка «ч:мм:сс» для оставшихся минут (логика _divmod оригинала).</summary>
    public string FormatRemaining(int minutes)
    {
        int seconds = Seconds;
        if (seconds < 60 && minutes > 0) minutes--;
        int hours = minutes / 60;
        minutes %= 60;
        return $"{hours,2}:{minutes:00}:{seconds % 60:00}";
    }
}
