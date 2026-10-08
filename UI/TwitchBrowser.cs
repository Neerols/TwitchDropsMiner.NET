using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TwitchDropsMiner.Core;

namespace TwitchDropsMiner.UI;

/// <summary>
/// Встроенный браузер (WebView2 = Microsoft Edge) с отдельным постоянным профилем в папке данных.
/// 1) Вход: пользователь сам входит на twitch.tv, мы забираем cookie auth-token и unique_id.
/// 2) Integrity: в скрытом окне открываем страницу кампаний и перехватываем integrity-токен,
///    который получает сам сайт Twitch.
/// Логин и пароль вводятся только на сайте Twitch и приложению не передаются.
/// </summary>
public static class TwitchBrowser
{
    private static CoreWebView2Environment? _env;
    private static string ProfileDir => Path.Combine(AppPaths.DataDir, "browser");

    // Облегчённый режим: без GPU, расширений и фоновых сервисов, не больше 2 процессов отрисовки,
    // ограниченная память JS — скрытому плееру на 160p этого достаточно.
    private const string BrowserArgs =
        "--disable-gpu --disable-gpu-compositing --disable-extensions --disable-background-networking " +
        "--disable-component-update --disable-sync --disable-features=Translate,MediaRouter,OptimizationHints " +
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
        await view.EnsureCoreWebView2Async(await EnvAsync());
        return (window, view);
    }

    private static async Task<(string? token, string? deviceId)> ReadCookiesAsync(CoreWebView2 core)
    {
        var cookies = await core.CookieManager.GetCookiesAsync("https://www.twitch.tv");
        string? token = cookies.FirstOrDefault(c => c.Name == "auth-token")?.Value;
        string? device = cookies.FirstOrDefault(c => c.Name == "unique_id")?.Value;
        return (string.IsNullOrEmpty(token) ? null : token, device);
    }

    /// <summary>Показать окно входа. Возвращает null, если пользователь закрыл окно.</summary>
    public static async Task<(string token, string? deviceId, string userAgent)?> LoginAsync(Window? owner)
    {
        var (window, view) = await CreateAsync(visible: true, owner);
        var core = view.CoreWebView2;
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        core.Navigate("https://www.twitch.tv/login");
        try
        {
            while (!closed.Task.IsCompleted)
            {
                await Task.WhenAny(Task.Delay(1000), closed.Task);
                if (closed.Task.IsCompleted) break;
                var (token, device) = await ReadCookiesAsync(core);
                if (token is not null)
                    return (token, device, core.Settings.UserAgent);
            }
            return null;
        }
        finally
        {
            if (!closed.Task.IsCompleted) window.Close();
        }
    }

    /// <summary>
    /// Открывает страницу twitch.tv в скрытом окне и собирает GQL-запросы/ответы, которые делает сам сайт
    /// (тело запроса содержит одну из операций из filter). Используется для получения данных,
    /// которые Twitch отдаёт только своему сайту (например, реальный прогресс дропов).
    /// </summary>
    public static async Task<List<(string request, string response)>> CaptureGqlAsync(
        string pageUrl, string[] filter, TimeSpan timeout, CancellationToken ct)
    {
        var (window, view) = await CreateAsync(visible: false, null);
        var core = view.CoreWebView2;
        var captured = new List<(string, string)>();
        try
        {
            core.AddWebResourceRequestedFilter("https://gql.twitch.tv/*", CoreWebView2WebResourceContext.All);
            core.WebResourceResponseReceived += async (_, e) =>
            {
                try
                {
                    if (!e.Request.Uri.StartsWith("https://gql.twitch.tv/gql", StringComparison.OrdinalIgnoreCase)) return;
                    string req = "";
                    if (e.Request.Content is { } rs)
                    {
                        using var sr = new StreamReader(rs);
                        req = await sr.ReadToEndAsync();
                    }
                    if (filter.Length > 0 && !filter.Any(f => req.Contains(f, StringComparison.Ordinal))) return;
                    using var body = await e.Response.GetContentAsync();
                    if (body is null) return;
                    using var br = new StreamReader(body);
                    var resp = await br.ReadToEndAsync();
                    lock (captured) captured.Add((req, resp));
                }
                catch { /* тело недоступно */ }
            };
            core.Navigate(pageUrl);
            await Task.Delay(timeout, ct);
            lock (captured) return captured.ToList();
        }
        catch (OperationCanceledException)
        {
            lock (captured) return captured.ToList();
        }
        finally
        {
            window.Close();
        }
    }

    #region Просмотр настоящим плеером

    private static Window? _watchWindow;
    private static WebView2? _watchView;
    private static string? _watchLogin;

    /// <summary>
    /// Смотреть канал настоящим плеером Twitch в скрытом окне: без звука, качество 160p.
    /// null — остановить просмотр.
    /// </summary>
    public static async Task WatchAsync(string? login)
    {
        if (login == _watchLogin && _watchView is not null) return;
        _watchLogin = login;
        if (login is null)
        {
            _watchWindow?.Close();
            _watchWindow = null;
            _watchView = null;
            return;
        }
        if (_watchView is null)
        {
            var (window, view) = await CreateAsync(visible: false, null);
            _watchWindow = window;
            _watchView = view;
            var core = view.CoreWebView2;
            core.IsMuted = true;
            // до загрузки страниц Twitch: минимальное качество и выключенный звук плеера
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "try{localStorage.setItem('video-quality','{\"default\":\"160p30\"}');" +
                "localStorage.setItem('video-muted','{\"default\":true}');" +
                "localStorage.setItem('volume','0');" +
                "localStorage.setItem('mature','true');}catch(e){}");
        }
        _watchView!.CoreWebView2.Navigate($"https://www.twitch.tv/{login}");
        Log.Info($"Browser player: watching {login}");
    }

    /// <summary>Состояние видео в скрытом плеере: «paused,currentTime,height» или null.</summary>
    public static async Task<string?> PlayerStateAsync()
    {
        if (_watchView?.CoreWebView2 is not { } core) return null;
        try
        {
            var r = await core.ExecuteScriptAsync(
                "(()=>{const v=document.querySelector('video');if(!v)return 'no-video';" +
                "if(v.paused){v.muted=true;v.play().catch(()=>{});}" +
                "return [v.paused?'paused':'playing',Math.round(v.currentTime),v.videoHeight].join(',');})()");
            return r.Trim('"');
        }
        catch { return null; }
    }

    /// <summary>Реальный прогресс с сайта Twitch: ответ Inventory, который запрашивает страница инвентаря.</summary>
    public static async Task<JsonNode?> FetchSiteInventoryAsync(CancellationToken ct)
    {
        var items = await CaptureGqlAsync("https://www.twitch.tv/drops/inventory", ["\"Inventory\""], TimeSpan.FromSeconds(20), ct);
        foreach (var (_, resp) in items)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(resp); } catch { continue; }
            IEnumerable<JsonNode?> list = node is JsonArray a ? a : [node];
            foreach (var r in list)
                if (r.At("data", "currentUser", "inventory") is { } inv) return inv;
        }
        return null;
    }

    #endregion

    /// <summary>Есть ли в профиле браузера сохранённый вход.</summary>
    public static async Task<bool> HasSessionAsync()
    {
        if (!Directory.Exists(ProfileDir)) return false;
        var (window, view) = await CreateAsync(visible: false, null);
        try { return (await ReadCookiesAsync(view.CoreWebView2)).token is not null; }
        finally { window.Close(); }
    }

    /// <summary>
    /// Открывает страницу кампаний в скрытом окне и перехватывает integrity-токен,
    /// который запрашивает сам сайт (ответ /integrity или заголовок Client-Integrity).
    /// </summary>
    public static async Task<string?> GetIntegrityTokenAsync(CancellationToken ct)
    {
        var (window, view) = await CreateAsync(visible: false, null);
        var core = view.CoreWebView2;
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            core.AddWebResourceRequestedFilter("https://gql.twitch.tv/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                try
                {
                    if (e.Request.Headers.Contains("Client-Integrity"))
                    {
                        var value = e.Request.Headers.GetHeader("Client-Integrity");
                        if (!string.IsNullOrEmpty(value)) result.TrySetResult(value);
                    }
                }
                catch { /* заголовка нет */ }
            };
            core.WebResourceResponseReceived += async (_, e) =>
            {
                try
                {
                    if (!e.Request.Uri.StartsWith("https://gql.twitch.tv/integrity", StringComparison.OrdinalIgnoreCase)) return;
                    using var stream = await e.Response.GetContentAsync();
                    if (stream is null) return;
                    var json = await JsonNode.ParseAsync(stream);
                    if (json.Str("token") is { Length: > 0 } token) result.TrySetResult(token);
                }
                catch { /* тело недоступно — ждём заголовок */ }
            };
            core.Navigate("https://www.twitch.tv/drops/campaigns");
            var done = await Task.WhenAny(result.Task, Task.Delay(TimeSpan.FromSeconds(60), ct));
            return done == result.Task ? result.Task.Result : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            window.Close();
        }
    }
}
