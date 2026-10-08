using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TwitchDropsMiner.Core;

/// <summary>
/// Состояние авторизации. Вход — только через встроенный браузер (сессия twitch.tv, веб-клиент).
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

    private Task? _integrityTask;
    private DateTime _integrityFailedAt = DateTime.MinValue;
    private static readonly TimeSpan IntegrityLifetime = TimeSpan.FromHours(4);
    private static readonly TimeSpan IntegrityRetryPause = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Получить (или обновить) integrity-токен. Все одновременные запросы ждут одну общую попытку;
    /// после неудачи новая попытка — не раньше чем через 10 минут (а не на каждый GQL-запрос).
    /// </summary>
    public async Task EnsureIntegrityAsync(bool force)
    {
        if (!ClientType.IsWeb(miner.Client)) return;
        bool fresh = IntegrityToken is not null && DateTime.UtcNow - _integrityAt < IntegrityLifetime;
        if (fresh && !force) return;
        if (_integrityTask is null)
        {
            if (DateTime.UtcNow - _integrityFailedAt < IntegrityRetryPause) return;
            _integrityTask = FetchIntegrityAsync();
        }
        try { await _integrityTask.WaitAsync(miner.Token); }
        finally { if (_integrityTask?.IsCompleted == true) _integrityTask = null; }
    }

    private async Task FetchIntegrityAsync()
    {
        var token = await miner.Ui.GetIntegrityTokenAsync(miner.Token);
        if (token is null)
        {
            _integrityFailedAt = DateTime.UtcNow;
            Log.Warning("Не удалось получить integrity-токен из браузера, следующая попытка через 10 минут");
            return;
        }
        IntegrityToken = token;
        _integrityAt = DateTime.UtcNow;
        _integrityFailedAt = DateTime.MinValue;
        Log.Info("Integrity-токен получен");
    }

    /// <summary>
    /// Результат входа через встроенный браузер: cookie auth-token и unique_id с twitch.tv.
    /// Если майнер ждёт входа — завершает ожидание; иначе сохраняет новый вход и перезапускает майнер.
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
        // Работает только вход через браузер: токены других клиентов (вход по коду, импорт) игнорируем
        if (AccessToken is null && !ClientType.IsWeb(ClientType.ById(stored.ClientId) ?? ClientType.Web))
        {
            Log.Info("Saved login is not a browser login, a new browser login is required");
            stored = new(null, null, null, null);
        }
        if (AccessToken is null) miner.Client = ClientType.Web.WithUserAgent(stored.UserAgent);
        DeviceId ??= stored.DeviceId;

        if (AccessToken is null || UserId == 0)
        {
            Log.Info("Checking login");
            miner.Ui.SetLoginStatus(L.T("gui.login.logging_in", "Logging in..."), null);
            AccessToken ??= stored.AccessToken;
            JsonNode? validate = null;
            for (int attempt = 0; attempt < 3 && validate is null; attempt++)
            {
                if (AccessToken is null)
                    AccessToken = await BrowserLoginAsync();
                else
                    Log.Info("Restoring session from saved token");
                var token = AccessToken;
                var (status, json) = await http.RequestJsonAsync(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate",
                    headers: h => h.TryAddWithoutValidation("Authorization", $"OAuth {token}"));
                if (status == 200 && json.Str("client_id") == ClientType.Web.ClientId)
                {
                    validate = json;
                    break;
                }
                if (status is not (200 or 401)) throw new MinerException($"Login verification failure (HTTP {status})");
                // токен недействителен или выдан не веб-клиенту — очищаем сессию в браузере и входим заново
                Log.Info("Saved session is invalid");
                AccessToken = null;
                await miner.Ui.ClearBrowserSessionAsync();
            }
            if (validate is null) throw new MinerException("Login verification failure");
            UserId = validate.Long("user_id");
            if (DeviceId is null)
            {
                await http.RequestAsync(HttpMethod.Get, miner.Client.ClientUrl.ToString(), headers: h => ApplyHeaders(h, false));
                DeviceId = http.GetCookie(miner.Client.ClientUrl, "unique_id") ?? Util.Nonce(Util.CharsHexLower, 32);
            }
            http.SetCookie(".twitch.tv", "auth-token", AccessToken!);
            http.SetCookie(".twitch.tv", "unique_id", DeviceId);
            SaveStored();
            Log.Info($"Login successful, user ID: {UserId}");
            miner.Ui.SetLoginStatus(L.T("gui.login.logged_in", "Logged in"), UserId);
        }
        miner.Ui.SetLogoutEnabled(true);
        _loggedIn.Set();
    }

    /// <summary>Ждём входа через встроенный браузер (окно открывается автоматически).</summary>
    private async Task<string> BrowserLoginAsync()
    {
        _browserLogin = new(TaskCreationOptions.RunContinuationsAsynchronously);
        miner.Ui.SetLoginStatus(L.T("gui.login.required", "Login required"), null);
        miner.Ui.SetStatus(L.T("gui.login.required", "Login required"));
        miner.Ui.GrabAttention();
        miner.Ui.RequestBrowserLogin();
        var (token, deviceId, userAgent) = await _browserLogin.Task.WaitAsync(miner.Token);
        miner.Client = ClientType.Web.WithUserAgent(userAgent);
        if (deviceId is not null) DeviceId = deviceId;
        IntegrityToken = null;
        return token;
    }

    /// <summary>Выход: отзыв токена на стороне Twitch и удаление сохранённой авторизации.</summary>
    public async Task RevokeAsync()
    {
        if (AccessToken is null)
        {
            Invalidate(deleteStored: true);
            await miner.Ui.ClearBrowserSessionAsync();
            return;
        }
        var token = AccessToken;
        var r = await miner.Http.RequestAsync(HttpMethod.Post, "https://id.twitch.tv/oauth2/revoke",
            () => new FormUrlEncodedContent([new("client_id", miner.Client.ClientId), new("token", token)]));
        if (r.Status != 200) Log.Error($"Failed to invalidate the auth token: {r.Status}");
        // локально выходим в любом случае: удаляем файл входа и сессию в браузере
        Invalidate(deleteStored: true);
        await miner.Ui.ClearBrowserSessionAsync();
    }
}
