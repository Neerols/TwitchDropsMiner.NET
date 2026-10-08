using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TwitchDropsMiner.Core;

namespace TwitchDropsMiner.UI;

/// <summary>
/// Встроенный браузер (WebView2 = Microsoft Edge) с отдельным постоянным профилем в папке данных.
/// 1) Вход: пользователь сам входит на twitch.tv, мы забираем cookie auth-token и unique_id.
/// 2) Служебный скрытый браузер: integrity-токен и реальный прогресс со страницы инвентаря.
/// 3) Плеер: скрытое окно, которое «смотрит» стрим без звука в 160p (под надзором).
/// Логин и пароль вводятся только на сайте Twitch и приложению не передаются.
/// Все методы вызываются в UI-потоке.
/// </summary>
public static class TwitchBrowser
{
    private static CoreWebView2Environment? _env;
    private static string ProfileDir => Path.Combine(AppPaths.DataDir, "browser");

    // Облегчённый режим: без GPU, расширений и фоновых сервисов, не больше 2 процессов отрисовки,
    // ограниченная память JS. Скрытые окна не должны «засыпать»: отключаем фоновое троттлинг.
    private const string BrowserArgs =
        "--disable-gpu --disable-gpu-compositing --disable-extensions --disable-background-networking " +
        "--disable-component-update --disable-sync " +
        "--disable-features=Translate,MediaRouter,OptimizationHints,CalculateNativeWinOcclusion " +
        "--disable-background-timer-throttling --disable-renderer-backgrounding " +
        "--disable-backgrounding-occluded-windows " +
        "--renderer-process-limit=2 --js-flags=--max-old-space-size=256 --mute-audio " +
        "--autoplay-policy=no-user-gesture-required";

    private static async Task<CoreWebView2Environment> EnvAsync() =>
        _env ??= await CoreWebView2Environment.CreateAsync(null, ProfileDir,
            new CoreWebView2EnvironmentOptions(BrowserArgs));

    /// <summary>Есть ли WebView2 Runtime. Другие ошибки не маскируются под «не установлен».</summary>
    public static bool IsRuntimeAvailable()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrEmpty(version)) return false;
            Log.Info($"WebView2 Runtime {version}");
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    private static async Task<(Window window, WebView2 view)> CreateAsync(bool visible, Window? owner)
    {
        var view = new WebView2();
        var window = new Window
        {
            Title = L.T("x.browser.title", "Twitch login"),
            Content = view,
            Width = 1000,
            Height = 820,
            WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner is { IsVisible: true } ? owner : null,
            Icon = owner?.Icon,
        };
        if (!visible)
        {
            // скрытое окно за пределами экрана: WebView2 нужен настоящий HWND
            window.WindowStyle = WindowStyle.None;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Owner = null;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.Width = 1000;
            window.Height = 700;
        }
        window.Show();
        try
        {
            await view.EnsureCoreWebView2Async(await EnvAsync());
        }
        catch
        {
            Destroy(window, view);
            throw;
        }
        return (window, view);
    }

    private static void Destroy(Window? window, WebView2? view)
    {
        try { view?.Dispose(); } catch { /* уже освобождён */ }
        try { window?.Close(); } catch { /* уже закрыт */ }
    }

    private static async Task<(string? token, string? deviceId)> ReadCookiesAsync(CoreWebView2 core)
    {
        var cookies = await core.CookieManager.GetCookiesAsync("https://www.twitch.tv");
        string? token = cookies.FirstOrDefault(c => c.Name == "auth-token")?.Value;
        string? device = cookies.FirstOrDefault(c => c.Name == "unique_id")?.Value;
        return (string.IsNullOrEmpty(token) ? null : token, device);
    }

    #region Вход и выход

    /// <summary>
    /// Показать окно входа. Возвращает null, если пользователь закрыл окно.
    /// Токен rejectedToken (уже отклонённый Twitch) не возвращается — ждём нового входа.
    /// </summary>
    public static async Task<(string token, string? deviceId, string userAgent)?> LoginAsync(Window? owner, string? rejectedToken = null)
    {
        var (window, view) = await CreateAsync(visible: true, owner);
        var core = view.CoreWebView2;
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        if (rejectedToken is not null) await DeleteSessionCookiesAsync(core);
        core.Navigate("https://www.twitch.tv/login");
        try
        {
            while (!closed.Task.IsCompleted)
            {
                await Task.WhenAny(Task.Delay(1000), closed.Task);
                if (closed.Task.IsCompleted) break;
                var (token, device) = await ReadCookiesAsync(core);
                if (token is not null && token != rejectedToken)
                    return (token, device, core.Settings.UserAgent);
            }
            return null;
        }
        finally
        {
            if (!closed.Task.IsCompleted) Destroy(window, view);
        }
    }

    private static async Task DeleteSessionCookiesAsync(CoreWebView2 core)
    {
        foreach (var c in await core.CookieManager.GetCookiesAsync("https://www.twitch.tv"))
            if (c.Name is "auth-token" or "persistent" or "login" or "name" or "twilight-user" or "api_token")
                core.CookieManager.DeleteCookie(c);
    }

    /// <summary>Удалить сессию Twitch из профиля браузера (выход или отклонённый токен).</summary>
    public static async Task ClearSessionAsync()
    {
        await WatchAsync(null);
        var core = await ServiceCoreAsync();
        await DeleteSessionCookiesAsync(core);
        _integrityToken = null;
        Log.Info("Browser session cleared");
    }

    #endregion

    #region Служебный скрытый браузер (integrity и инвентарь)

    private static Window? _serviceWindow;
    private static WebView2? _serviceView;
    private static readonly SemaphoreSlim _serviceLock = new(1, 1);
    private static string? _integrityToken;

    private static async Task<CoreWebView2> ServiceCoreAsync()
    {
        if (_serviceView?.CoreWebView2 is { } existing) return existing;
        var (window, view) = await CreateAsync(visible: false, null);
        _serviceWindow = window;
        _serviceView = view;
        var core = view.CoreWebView2;
        core.IsMuted = true;
        core.AddWebResourceRequestedFilter("https://gql.twitch.tv/*", CoreWebView2WebResourceContext.All);
        // сайт сам получает integrity-токен — запоминаем его из заголовков запросов
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                if (e.Request.Headers.Contains("Client-Integrity")
                    && e.Request.Headers.GetHeader("Client-Integrity") is { Length: > 0 } v)
                    _integrityToken = v;
            }
            catch { /* заголовка нет */ }
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Warning($"Service browser process failed: {e.ProcessFailedKind}");
            var (w, v) = (_serviceWindow, _serviceView);
            _serviceWindow = null;
            _serviceView = null;
            Destroy(w, v);
        };
        return core;
    }

    /// <summary>
    /// Открывает страницу в служебном браузере и ждёт первый GQL-ответ, на котором accept вернёт результат.
    /// После этого страница выгружается (about:blank), чтобы не держать в памяти SPA Twitch.
    /// </summary>
    private static async Task<T?> CaptureAsync<T>(string pageUrl, Func<string, string, T?> accept, TimeSpan timeout, CancellationToken ct)
        where T : class
    {
        await _serviceLock.WaitAsync(ct);
        CoreWebView2? core = null;
        EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs>? handler = null;
        try
        {
            core = await ServiceCoreAsync();
            var result = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler = async (_, e) =>
            {
                try
                {
                    var uri = e.Request.Uri;
                    if (!uri.StartsWith("https://gql.twitch.tv/", StringComparison.OrdinalIgnoreCase)) return;
                    string req = "";
                    if (e.Request.Content is { } rs)
                    {
                        using var sr = new StreamReader(rs);
                        req = await sr.ReadToEndAsync();
                    }
                    using var body = await e.Response.GetContentAsync();
                    if (body is null) return;
                    using var br = new StreamReader(body);
                    var resp = await br.ReadToEndAsync();
                    if (accept(uri + "\n" + req, resp) is { } value) result.TrySetResult(value);
                }
                catch { /* тело недоступно */ }
            };
            core.WebResourceResponseReceived += handler;
            core.Navigate(pageUrl);
            var done = await Task.WhenAny(result.Task, Task.Delay(timeout, ct));
            return done == result.Task ? result.Task.Result : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (core is not null)
            {
                if (handler is not null) core.WebResourceResponseReceived -= handler;
                try { core.Navigate("about:blank"); } catch { /* браузер уже закрыт */ }
            }
            _serviceLock.Release();
        }
    }

    /// <summary>Integrity-токен, который получает сам сайт (ответ /integrity или заголовок Client-Integrity).</summary>
    public static async Task<string?> GetIntegrityTokenAsync(CancellationToken ct)
    {
        _integrityToken = null;
        var token = await CaptureAsync("https://www.twitch.tv/drops/campaigns", (request, response) =>
        {
            if (request.StartsWith("https://gql.twitch.tv/integrity", StringComparison.OrdinalIgnoreCase))
            {
                try { return JsonNode.Parse(response).Str("token") is { Length: > 0 } t ? t : null; }
                catch { return null; }
            }
            return _integrityToken;  // заголовок из любого запроса сайта
        }, TimeSpan.FromSeconds(45), ct);
        return token ?? _integrityToken;
    }

    /// <summary>Реальный прогресс с сайта Twitch: ответ Inventory, который запрашивает страница инвентаря.</summary>
    public static Task<JsonNode?> FetchSiteInventoryAsync(CancellationToken ct) =>
        CaptureAsync<JsonNode>("https://www.twitch.tv/drops/inventory", (request, response) =>
        {
            if (!request.Contains("\"Inventory\"", StringComparison.Ordinal)) return null;
            JsonNode? node;
            try { node = JsonNode.Parse(response); } catch { return null; }
            IEnumerable<JsonNode?> list = node is JsonArray a ? a : [node];
            foreach (var r in list)
                if (r.At("data", "currentUser", "inventory") is { } inv) return inv.DeepClone();
            return null;
        }, TimeSpan.FromSeconds(30), ct);

    #endregion

    #region Плеер (под надзором)

    private static Window? _watchWindow;
    private static WebView2? _watchView;
    private static string? _watchLogin;     // что сейчас загружено в плеер
    private static string? _wantedLogin;    // что должно быть загружено
    private static DateTime _loadedAt;
    private static int _badTicks;
    private static readonly SemaphoreSlim _watchLock = new(1, 1);
    private static readonly TimeSpan PlannedReload = TimeSpan.FromHours(3);

    /// <summary>Смотреть канал настоящим плеером в скрытом окне (без звука, 160p). null — остановить.</summary>
    public static async Task WatchAsync(string? login)
    {
        _wantedLogin = login;
        await _watchLock.WaitAsync();
        try
        {
            await ApplyWantedAsync(forceReload: false);
        }
        finally { _watchLock.Release(); }
    }

    private static async Task ApplyWantedAsync(bool forceReload)
    {
        var login = _wantedLogin;
        if (login is null)
        {
            if (_watchView is not null) Log.Info("Browser player: stopped");
            DestroyPlayer();
            return;
        }
        if (!forceReload && login == _watchLogin && _watchView?.CoreWebView2 is not null) return;
        if (_watchView?.CoreWebView2 is null)
        {
            DestroyPlayer();
            var (window, view) = await CreateAsync(visible: false, null);
            _watchWindow = window;
            _watchView = view;
            var core = view.CoreWebView2;
            core.IsMuted = true;
            core.ProcessFailed += (_, e) =>
            {
                Log.Warning($"Browser player process failed: {e.ProcessFailedKind}");
                DestroyPlayer();  // восстановится на следующей проверке
            };
            // до загрузки страниц Twitch: минимальное качество и выключенный звук плеера
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "try{localStorage.setItem('video-quality','{\"default\":\"160p30\"}');" +
                "localStorage.setItem('video-muted','{\"default\":true}');" +
                "localStorage.setItem('volume','0');" +
                "localStorage.setItem('mature','true');}catch(e){}");
            // пока создавали окно, канал могли сменить или остановить
            login = _wantedLogin;
            if (login is null) { DestroyPlayer(); return; }
        }
        _watchView!.CoreWebView2.Navigate($"https://www.twitch.tv/{login}");
        _watchLogin = login;
        _loadedAt = DateTime.UtcNow;
        _badTicks = 0;
        Log.Info($"Browser player: watching {login}");
    }

    private static void DestroyPlayer()
    {
        var (w, v) = (_watchWindow, _watchView);
        _watchWindow = null;
        _watchView = null;
        _watchLogin = null;
        Destroy(w, v);
    }

    /// <summary>
    /// Проверка плеера (раз в минуту): если видео нет или оно стоит 3 проверки подряд, процесс упал
    /// или плеер работает дольше 3 часов — перезагружаем. Возвращает состояние для интерфейса.
    /// </summary>
    public static async Task<string> PlayerTickAsync()
    {
        if (_wantedLogin is null) return "off";
        await _watchLock.WaitAsync();
        try
        {
            if (_watchView?.CoreWebView2 is not { } core)
            {
                Log.Warning("Browser player is not running, restarting");
                await ApplyWantedAsync(forceReload: true);
                return "restarting";
            }
            string state;
            try
            {
                var r = await core.ExecuteScriptAsync(
                    "(()=>{const v=document.querySelector('video');if(!v)return 'no-video';" +
                    "if(v.paused){v.muted=true;v.play().catch(()=>{});}" +
                    "return [v.paused?'paused':'playing',Math.round(v.currentTime),v.videoHeight].join(',');})()");
                state = r.Trim('"');
            }
            catch (Exception ex)
            {
                state = "error: " + ex.Message;
            }
            _badTicks = state.StartsWith("playing") ? 0 : _badTicks + 1;
            if (_badTicks >= 3 || DateTime.UtcNow - _loadedAt > PlannedReload)
            {
                Log.Warning($"Browser player reload ({(_badTicks >= 3 ? "not playing: " + state : "planned")})");
                await ApplyWantedAsync(forceReload: true);
                return "restarting";
            }
            return state;
        }
        finally { _watchLock.Release(); }
    }

    #endregion
}
