using System.Net;
using System.Text.Json.Nodes;

namespace TwitchDropsMiner.Core;

/// <summary>
/// Публичный каталог кампаний Twitch Drops (twitch-drops-api.sunkwi.com).
///
/// С сентября 2026 Twitch закрыл ViewerDropsDashboard / DropCampaignDetails integrity-проверкой:
/// для клиентов, которые могут войти по коду (SmartBox, Android), список кампаний приходит пустым.
/// Каталог используется только для публичных описаний кампаний; инвентарь, прогресс, просмотр
/// и получение наград по-прежнему идут напрямую через Twitch. Запрос делается отдельным
/// HTTP-клиентом без cookie — токен Twitch туда никогда не отправляется.
/// </summary>
public static class CampaignCatalog
{
    public const string Url = "https://twitch-drops-api.sunkwi.com/v2/drops";

    public static async Task<JsonObject> FetchAsync(Settings settings, CancellationToken ct)
    {
        var result = new JsonObject();
        JsonNode? payload;
        try
        {
            var proxy = HttpService.BuildProxy(settings.Proxy);
            using var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                Proxy = proxy,
                UseProxy = proxy is not null,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TwitchDropsMiner.NET campaign catalog");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await client.GetAsync(Url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warning($"Campaign catalog returned HTTP {(int)resp.StatusCode}");
                return result;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            payload = JsonNode.Parse(bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning($"Campaign catalog request failed: {ex.Message}");
            return result;
        }

        // устаревший снимок не используем, чтобы не показывать закончившиеся кампании
        var now = DateTimeOffset.UtcNow;
        var updated = payload.Time("lastUpdatedAt");
        int refresh = Math.Max(1, payload.Int("refreshIntervalSeconds"));
        var maxAge = TimeSpan.FromSeconds(Math.Max(1800, refresh * 30));
        if (updated == DateTimeOffset.MinValue || now - updated > maxAge || updated > now.AddMinutes(5))
        {
            Log.Warning($"Campaign catalog is stale (updated {updated:u}), ignoring it");
            return result;
        }

        foreach (var group in payload.Arr("data"))
        {
            var boxArt = group.Str("gameBoxArtURL") ?? "";
            foreach (var raw in group.Arr("rewards"))
            {
                if (raw is not JsonObject c || c.Str("id") is not { } id) continue;
                if (c.Str("status") is not ("ACTIVE" or "UPCOMING")) continue;
                if (c.Time("endAt") <= now) continue;
                if (c["game"] is not JsonObject game) continue;

                var campaign = (JsonObject)c.DeepClone();
                var g = (JsonObject)campaign["game"]!;
                if (g.Str("boxArtURL") is null) g["boxArtURL"] = boxArt;
                // каталог не знает, привязан ли аккаунт; данные инвентаря Twitch (если есть) это перекроют
                if (campaign["self"] is not JsonObject) campaign["self"] = new JsonObject { ["isAccountConnected"] = true };
                if (campaign["allow"] is not JsonObject) campaign["allow"] = new JsonObject { ["isEnabled"] = false, ["channels"] = null };
                if (campaign["timeBasedDrops"] is not JsonArray) campaign["timeBasedDrops"] = new JsonArray();
                campaign["accountLinkURL"] ??= "https://www.twitch.tv/drops/campaigns";
                result[id] = campaign;
            }
        }
        Log.Info($"Campaign catalog: {result.Count} campaigns");
        return result;
    }
}
