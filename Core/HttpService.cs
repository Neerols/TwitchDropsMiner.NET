using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace TwitchDropsMiner.Core;

public sealed class HttpResult(int status, byte[] body, Uri? finalUri)
{
    public int Status { get; } = status;
    public byte[] Body { get; } = body;
    public Uri? FinalUri { get; } = finalUri;
    public string Text => Encoding.UTF8.GetString(Body);

    public JsonNode? Json()
    {
        try { return JsonNode.Parse(Body); }
        catch (System.Text.Json.JsonException ex)
        {
            throw new MinerException(L.T("login.unexpected_content", "Unexpected content type returned"), ex);
        }
    }
}

/// <summary>
/// HTTP-сессия: один HttpClient на всё приложение, общий CookieContainer, прокси,
/// таймауты по «качеству соединения» и повторы с экспоненциальной задержкой.
/// Тело ответа читается и разбирается вне UI-потока.
/// </summary>
public sealed class HttpService : IDisposable
{
    private readonly CancellationToken _ct;
    private readonly Action<string> _print;
    public CookieContainer Cookies { get; } = new();
    public IWebProxy? Proxy { get; }
    public TimeSpan TotalTimeout { get; }

    private readonly Func<ClientInfo> _client;

    public HttpService(Settings settings, Func<ClientInfo> client, Action<string> print, CancellationToken ct)
    {
        _ct = ct;
        _print = print;
        _client = client;
        int q = Math.Clamp(settings.ConnectionQuality, 1, 6);
        Proxy = BuildProxy(settings.Proxy);
        var handler = new SocketsHttpHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5 * q),
            MaxConnectionsPerServer = 50,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            Proxy = Proxy,
            UseProxy = Proxy is not null,
        };
        TotalTimeout = TimeSpan.FromSeconds(10 * q);
        _http = new HttpClient(handler) { Timeout = TotalTimeout };
    }

    private readonly HttpClient _http;

    public static IWebProxy? BuildProxy(string proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return null;
        if (!Uri.TryCreate(proxy.Trim(), UriKind.Absolute, out var uri) || uri.Port <= 0) return null;
        var wp = new WebProxy(new Uri($"{uri.Scheme}://{uri.Host}:{uri.Port}"));
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            wp.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return wp;
    }

    public static bool IsValidProxy(string proxy) =>
        string.IsNullOrWhiteSpace(proxy) || BuildProxy(proxy) is not null;

    /// <summary>
    /// Запрос с повторами: при ошибках сети и ответах 5xx повторяем с задержкой до 3 минут.
    /// invalidateAfter — момент, после которого запрос теряет смысл (бросается RequestInvalidException).
    /// </summary>
    public async Task<HttpResult> RequestAsync(
        HttpMethod method,
        string url,
        Func<HttpContent?>? content = null,
        Action<HttpRequestHeaders>? headers = null,
        DateTimeOffset? invalidateAfter = null)
    {
        Log.Debug($"Request: {method} {url}");
        var backoff = new ExponentialBackoff(maximum: 180);
        while (true)
        {
            _ct.ThrowIfCancellationRequested();
            double delay = backoff.Next();
            if (invalidateAfter is not null && DateTimeOffset.UtcNow >= invalidateAfter.Value - TotalTimeout)
                throw new RequestInvalidException();
            try
            {
                using var req = new HttpRequestMessage(method, url);
                req.Content = content?.Invoke();
                req.Headers.TryAddWithoutValidation("User-Agent", _client().UserAgent);
                headers?.Invoke(req.Headers);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, _ct).ConfigureAwait(false);
                int status = (int)resp.StatusCode;
                Log.Debug($"Response: {status} {url}");
                if (status < 500)
                {
                    var body = await resp.Content.ReadAsByteArrayAsync(_ct).ConfigureAwait(false);
                    return new HttpResult(status, body, resp.RequestMessage?.RequestUri);
                }
                _print(L.F("error.site_down", "Twitch is down, retrying in {seconds} seconds...", ("seconds", Math.Round(delay))));
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                if (ex is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
                    throw new MinerException($"SSL error: {ex.Message}", ex);
                if (backoff.Steps > 1)
                    _print(L.F("error.no_connection", "Cannot connect to Twitch, retrying in {seconds} seconds...",
                        ("seconds", Math.Round(delay)), ("url", url)));
                Log.Debug($"Connection problem ({url}): {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(delay), _ct).ConfigureAwait(false);
        }
    }

    /// <summary>Одна попытка GET без повторов: null при ошибке или статусе ≥ 400.</summary>
    public async Task<string?> TryGetStringOnceAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", _client().UserAgent);
            using var resp = await _http.SendAsync(req, _ct).ConfigureAwait(false);
            if ((int)resp.StatusCode >= 400)
            {
                Log.Warning($"GET {url} returned {(int)resp.StatusCode}");
                return null;
            }
            return await resp.Content.ReadAsStringAsync(_ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !_ct.IsCancellationRequested)
        {
            Log.Warning($"GET {url} failed: {ex.Message}");
            return null;
        }
    }

    public async Task<string> GetStringAsync(string url) =>
        (await RequestAsync(HttpMethod.Get, url).ConfigureAwait(false)).Text;

    public async Task<int> PostStatusAsync(string url, Func<HttpContent> content) =>
        (await RequestAsync(HttpMethod.Post, url, content).ConfigureAwait(false)).Status;

    /// <summary>То же, что RequestAsync, но с разбором JSON вне UI-потока.</summary>
    public async Task<(int status, JsonNode? json)> RequestJsonAsync(
        HttpMethod method, string url, Func<HttpContent?>? content = null,
        Action<HttpRequestHeaders>? headers = null, DateTimeOffset? invalidateAfter = null)
    {
        var r = await RequestAsync(method, url, content, headers, invalidateAfter).ConfigureAwait(false);
        return (r.Status, r.Body.Length == 0 ? null : r.Json());
    }

    public string? GetCookie(Uri uri, string name) => Cookies.GetCookies(uri)[name]?.Value;

    public void SetCookie(string domain, string name, string value)
    {
        try { Cookies.Add(new Cookie(name, value, "/", domain)); }
        catch (CookieException ex) { Log.Debug($"Cookie {name}: {ex.Message}"); }
    }

    public void Dispose() => _http.Dispose();
}
