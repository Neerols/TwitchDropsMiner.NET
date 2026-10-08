using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TwitchDropsMiner.Core;

/// <summary>
/// Состояние авторизации. Вход — через OAuth Device Code Flow (код вводится на twitch.tv/activate).
/// Токен хранится в config/auth.bin, зашифрованный DPAPI (доступен только текущему пользователю Windows).
/// </summary>
public sealed class AuthState(Miner miner)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly AsyncEvent _loggedIn = new();

    public long UserId { get; private set; }
    public string? DeviceId { get; private set; }
    public string? SessionId { get; private set; }
    public string? AccessToken { get; private set; }

    public bool IsLoggedIn => _loggedIn.IsSet;
    public Task WaitUntilLoginAsync(CancellationToken ct) => _loggedIn.WaitAsync(ct);

    #region Хранение токена

    private sealed record Stored(string? AccessToken, string? DeviceId, string? ClientId, string? UserAgent);

    private static Stored LoadStored()
    {
        try
        {
            if (!File.Exists(AppPaths.AuthFile)) return new(null, null, null, null);
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.AuthFile), null, DataProtectionScope.CurrentUser);
            var node = JsonNode.Parse(raw);
            return new(node.Str("access_token"), node.Str("device_id"), node.Str("client_id"), node.Str("user_agent"));
        }
        catch (Exception ex)
        {
            Log.Warning($"Не удалось прочитать сохранённую авторизацию: {ex.Message}");
            return new(null, null, null, null);
        }
    }

    private void SaveStored()
    {
        var node = new JsonObject
        {
            ["access_token"] = AccessToken,
            ["device_id"] = DeviceId,
            ["client_id"] = miner.Client.ClientId,
            // для веб-входа важен User-Agent браузера: к нему привязан integrity-токен
            ["user_agent"] = ClientType.IsWeb(miner.Client) ? miner.Client.UserAgent : null,
        };
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(node.ToJsonString()), null, DataProtectionScope.CurrentUser);
        AppPaths.EnsureDirs();
        File.WriteAllBytes(AppPaths.AuthFile, data);
    }

    #endregion

    /// <summary>Сбросить токен; deleteStored — удалить файл авторизации («выйти»).</summary>
    public void Invalidate(bool deleteStored)
    {
        AccessToken = null;
        UserId = 0;
        _loggedIn.Clear();
        miner.Ui.SetLogoutEnabled(false);
        if (deleteStored)
        {
            try { File.Delete(AppPaths.AuthFile); } catch { /* нет файла — нечего удалять */ }
        }
    }

    public void Clear()
    {
        UserId = 0;
        DeviceId = null;
        SessionId = null;
        AccessToken = null;
        _loggedIn.Clear();
        miner.Ui.SetLogoutEnabled(false);
    }

    /// <summary>Заголовки для запросов к Twitch.</summary>
    public void ApplyHeaders(HttpRequestHeaders h, bool gql)
    {
        var client = miner.Client;
        h.TryAddWithoutValidation("Accept", "*/*");
        h.TryAddWithoutValidation("Accept-Language", "en-US");
        h.TryAddWithoutValidation("Pragma", "no-cache");
        h.TryAddWithoutValidation("Cache-Control", "no-cache");
        h.TryAddWithoutValidation("Client-Id", client.ClientId);
        if (SessionId is not null) h.TryAddWithoutValidation("Client-Session-Id", SessionId);
        if (DeviceId is not null) h.TryAddWithoutValidation("X-Device-Id", DeviceId);
        if (gql)
        {
            var origin = client.ClientUrl.ToString().TrimEnd('/');
            h.TryAddWithoutValidation("Origin", origin);
            h.TryAddWithoutValidation("Referer", origin);
            h.TryAddWithoutValidation("Authorization", $"OAuth {AccessToken}");
            if (ClientType.IsWeb(client) && IntegrityToken is not null)
                h.TryAddWithoutValidation("Client-Integrity", IntegrityToken);
        }
    }

    #region Веб-вход и integrity

    /// <summary>Integrity-токен веб-клиента (получается из встроенного браузера).</summary>
    public string? IntegrityToken { get; private set; }
    private DateTime _integrityAt;
    private TaskCompletionSource<(string token, string? deviceId, string userAgent)>? _browserLogin;

    /// <summary>Получить (или обновить) integrity-токен для веб-клиента. Для других клиентов — no-op.</summary>
    private readonly SemaphoreSlim _integrityLock = new(1, 1);

    public async Task EnsureIntegrityAsync(bool force)
    {
        if (!ClientType.IsWeb(miner.Client)) return;
        if (!force && IntegrityToken is not null && DateTime.UtcNow - _integrityAt < TimeSpan.FromHours(4)) return;
        var requestedAt = DateTime.UtcNow;
        await _integrityLock.WaitAsync(miner.Token);
        try
        {
            // пока ждали блокировку, токен мог обновить другой запрос
            if (_integrityAt >= requestedAt && IntegrityToken is not null) return;
            await FetchIntegrityAsync();
        }
        finally { _integrityLock.Release(); }
    }

    private async Task FetchIntegrityAsync()
    {
        var token = await miner.Ui.GetIntegrityTokenAsync(miner.Token);
        if (token is null)
        {
            Log.Warning("Не удалось получить integrity-токен из браузера");
            return;
        }
        IntegrityToken = token;
        _integrityAt = DateTime.UtcNow;
        Log.Info("Integrity-токен получен");
    }

    /// <summary>
    /// Результат входа через встроенный браузер: cookie auth-token и unique_id с twitch.tv.
    /// Если идёт вход по коду — завершает его; иначе сохраняет новый вход и перезапускает майнер.
    /// </summary>
    public void SubmitBrowserLogin(string token, string? deviceId, string userAgent)
    {
        if (_browserLogin is { Task.IsCompleted: false } pending)
        {
            pending.TrySetResult((token, deviceId, userAgent));
            return;
        }
        miner.Client = ClientType.Web.WithUserAgent(userAgent);
        AccessToken = token;
        DeviceId = deviceId ?? DeviceId;
        IntegrityToken = null;
        UserId = 0;
        SaveStored();
        miner.ChangeState(MinerState.Restart);
    }

    #endregion

    public async Task ValidateAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { await ValidateInternalAsync(); }
        finally { _lock.Release(); }
    }

    private async Task ValidateInternalAsync()
    {
        SessionId ??= Util.Nonce(Util.CharsHexLower, 16);
        if (DeviceId is not null && AccessToken is not null && UserId != 0)
        {
            _loggedIn.Set();
            return;
        }
        var http = miner.Http;
        var stored = LoadStored();
        // токен привязан к клиенту, которым он получен
        if (AccessToken is null && ClientType.ById(stored.ClientId) is { } storedClient)
            miner.Client = ClientType.IsWeb(storedClient) ? storedClient.WithUserAgent(stored.UserAgent) : storedClient;
        var client = miner.Client;
        if (DeviceId is null)
        {
            DeviceId = stored.DeviceId;
            if (DeviceId is null)
            {
                // запрос главной страницы выставляет cookie unique_id — это и есть device id
                await http.RequestAsync(HttpMethod.Get, client.ClientUrl.ToString(), headers: h => ApplyHeaders(h, false));
                DeviceId = http.GetCookie(client.ClientUrl, "unique_id") ?? Util.Nonce(Util.CharsHexLower, 32);
            }
            http.SetCookie(".twitch.tv", "unique_id", DeviceId);
        }

        if (AccessToken is null || UserId == 0)
        {
            Log.Info("Checking login");
            miner.Ui.SetLoginStatus(L.T("gui.login.logging_in", "Logging in..."), null);
            AccessToken ??= stored.AccessToken;
            JsonNode? validate = null;
            for (int clientMismatch = 0; clientMismatch < 2 && validate is null; clientMismatch++)
            {
                for (int invalidToken = 0; invalidToken < 2; invalidToken++)
                {
                    if (AccessToken is null)
                        AccessToken = await DeviceCodeLoginAsync();
                    else
                        Log.Info("Restoring session from saved token");
                    var token = AccessToken;
                    var (status, json) = await http.RequestJsonAsync(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate",
                        headers: h => h.TryAddWithoutValidation("Authorization", $"OAuth {token}"));
                    if (status == 401)
                    {
                        Log.Info("Restored session is invalid");
                        AccessToken = null;
                        continue;
                    }
                    if (status == 200) { validate = json; break; }
                    throw new MinerException($"Login verification failure (HTTP {status})");
                }
                if (validate is null) throw new MinerException("Login verification failure (step #2)");
                var tokenClientId = validate.Str("client_id");
                if (tokenClientId != miner.Client.ClientId)
                {
                    if (ClientType.ById(tokenClientId) is { } known)
                    {
                        // токен выдан другому известному клиенту — просто переключаемся на него
                        miner.Client = ClientType.IsWeb(known) ? known.WithUserAgent(stored.UserAgent) : known;
                    }
                    else
                    {
                        // неизвестный клиент — нужен новый вход
                        Log.Info("Token client ID mismatch");
                        AccessToken = null;
                        validate = null;
                    }
                }
            }
            if (validate is null) throw new MinerException("Login verification failure (step #1)");
            UserId = validate.Long("user_id");
            http.SetCookie(".twitch.tv", "auth-token", AccessToken!);
            if (DeviceId is not null) http.SetCookie(".twitch.tv", "unique_id", DeviceId);
            SaveStored();
            Log.Info($"Login successful, user ID: {UserId}");
            miner.Ui.SetLoginStatus(L.T("gui.login.logged_in", "Logged in"), UserId);
        }
        miner.Ui.ShowDeviceCode(null, null);
        miner.Ui.SetLogoutEnabled(true);
        _loggedIn.Set();
    }

    private void OAuthHeaders(HttpRequestHeaders h)
    {
        var c = miner.Client;
        var origin = c.ClientUrl.ToString().TrimEnd('/');
        h.TryAddWithoutValidation("Accept", "application/json");
        h.TryAddWithoutValidation("Accept-Language", "en-US");
        h.TryAddWithoutValidation("Cache-Control", "no-cache");
        h.TryAddWithoutValidation("Client-Id", c.ClientId);
        h.TryAddWithoutValidation("Origin", origin);
        h.TryAddWithoutValidation("Pragma", "no-cache");
        h.TryAddWithoutValidation("Referer", origin);
        h.TryAddWithoutValidation("X-Device-Id", DeviceId);
    }

    /// <summary>OAuth Device Code Flow: получаем код, показываем его пользователю и ждём подтверждения.</summary>
    private async Task<string> DeviceCodeLoginAsync()
    {
        var http = miner.Http;
        _browserLogin = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = _browserLogin.Task;
        var candidates = ClientType.Candidates.OrderBy(c => c.ClientId == miner.Client.ClientId ? 0 : 1).ToList();
        int candidate = 0;
        while (true)
        {
            try
            {
                miner.Client = candidates[candidate];
                var clientId = miner.Client.ClientId;
                var now = DateTimeOffset.UtcNow;
                var (status, resp) = await http.RequestJsonAsync(HttpMethod.Post, "https://id.twitch.tv/oauth2/device",
                    () => new FormUrlEncodedContent([new("client_id", clientId), new("scopes", "")]), OAuthHeaders);
                if (status == 400 && resp.Str("message") == "invalid client" && candidate + 1 < candidates.Count)
                {
                    // Twitch больше не принимает этот клиент для входа по коду — пробуем следующий
                    Log.Warning($"Client {clientId} rejected for device login, trying another client");
                    candidate++;
                    continue;
                }
                var deviceCode = resp.Str("device_code")
                    ?? throw new MinerException($"Device login failed: HTTP {status} {resp?.ToJsonString()}");
                var userCode = resp.Str("user_code") ?? "";
                var interval = Math.Max(1, resp.Int("interval"));
                var verificationUri = resp.Str("verification_uri") ?? "https://www.twitch.tv/activate";
                var expiresAt = now.AddSeconds(resp.Int("expires_in"));

                miner.Ui.SetLoginStatus(L.T("gui.login.required", "Login required"), null);
                miner.Ui.SetStatus(L.T("gui.login.required", "Login required"));
                miner.Ui.ShowDeviceCode(userCode, verificationUri);
                miner.Ui.GrabAttention();
                miner.Print(L.T("gui.login.request", "Please log in to continue."));
                miner.Print(L.F("x.login.code_print", null, ("code", userCode)));

                while (true)
                {
                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(interval), miner.Token), browser);
                    miner.Token.ThrowIfCancellationRequested();
                    if (browser.IsCompleted)
                    {
                        // пользователь вошёл через встроенный браузер
                        var (bToken, bDevice, bUa) = browser.Result;
                        miner.Client = ClientType.Web.WithUserAgent(bUa);
                        if (bDevice is not null) DeviceId = bDevice;
                        miner.Ui.ShowDeviceCode(null, null);
                        return bToken;
                    }
                    var (tokenStatus, tokenResp) = await http.RequestJsonAsync(HttpMethod.Post, "https://id.twitch.tv/oauth2/token",
                        () => new FormUrlEncodedContent([
                            new("client_id", clientId),
                            new("device_code", deviceCode),
                            new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                        ]), OAuthHeaders, expiresAt);
                    // 200 — успех, 400 — пользователь ещё не ввёл код
                    if (tokenStatus != 200) continue;
                    miner.Ui.ShowDeviceCode(null, null);
                    return tokenResp.Str("access_token") ?? throw new MinerException("No access_token in response");
                }
            }
            catch (RequestInvalidException)
            {
                // срок действия кода истёк — запрашиваем новый
            }
        }
    }

    /// <summary>
    /// Импорт входа из cookies.jar Python-версии. Токен проверяется через oauth2/validate и сохраняется
    /// только если Twitch его принял. Возвращает текст результата для журнала.
    /// </summary>
    public async Task<string> ImportLegacyAsync(string path)
    {
        var (token, deviceId) = LegacyCookies.Read(path);
        if (token is null) return L.T("x.import.no_token", "No auth-token found in this file.");
        var (status, json) = await miner.Http.RequestJsonAsync(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate",
            headers: h => h.TryAddWithoutValidation("Authorization", $"OAuth {token}"));
        if (status != 200) return L.F("x.import.invalid", "The token from this file is not valid (HTTP {status}).", ("status", status));
        var client = ClientType.ById(json.Str("client_id"));
        if (client is null) return L.F("x.import.unknown_client", "Unknown Twitch client: {client}", ("client", json.Str("client_id")));
        miner.Client = client;
        AccessToken = token;
        DeviceId = deviceId ?? DeviceId;
        UserId = 0;  // заново проверится при перезапуске
        SaveStored();
        return L.F("x.import.ok", "Login imported (user ID {user}), restarting...", ("user", json.Long("user_id")));
    }

    /// <summary>Выход: отзыв токена на стороне Twitch и удаление сохранённой авторизации.</summary>
    public async Task RevokeAsync()
    {
        if (AccessToken is null) return;
        var token = AccessToken;
        var r = await miner.Http.RequestAsync(HttpMethod.Post, "https://id.twitch.tv/oauth2/revoke",
            () => new FormUrlEncodedContent([new("client_id", miner.Client.ClientId), new("token", token)]));
        if (r.Status == 200) Invalidate(deleteStored: true);
        else Log.Error($"Failed to invalidate the auth token: {r.Status}");
    }
}
