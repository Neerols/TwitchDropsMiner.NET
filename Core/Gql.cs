using System.Text.Json.Nodes;

namespace TwitchDropsMiner.Core;

/// <summary>
/// Persisted GQL-запросы Twitch (имена операций и sha256 взяты из оригинального проекта).
/// Каждый вызов возвращает новый JsonObject, так что их можно безопасно изменять.
/// </summary>
public static class Gql
{
    private static JsonObject Op(string name, string sha256, JsonObject? variables = null)
    {
        var op = new JsonObject
        {
            ["operationName"] = name,
            ["extensions"] = new JsonObject
            {
                ["persistedQuery"] = new JsonObject { ["version"] = 1, ["sha256Hash"] = sha256 },
            },
        };
        if (variables is not null) op["variables"] = variables;
        return op;
    }

    /// <summary>Информация о стриме канала.</summary>
    public static JsonObject GetStreamInfo(string channelLogin) => Op(
        "VideoPlayerStreamInfoOverlayChannel",
        "198492e0857f6aedead9665c81c5a06d67b25b58034649687124083ff288597d",
        new JsonObject { ["channel"] = channelLogin });

    /// <summary>Забрать награду (дроп).</summary>
    public static JsonObject ClaimDrop(string dropInstanceId) => Op(
        "DropsPage_ClaimDropRewards",
        "a455deea71bdc9015b78eb49f4acfbce8baa7ccbedd28e549bb025bd0f751930",
        new JsonObject { ["input"] = new JsonObject { ["dropInstanceID"] = dropInstanceId } });

    /// <summary>Кампании в процессе (инвентарь).</summary>
    public static JsonObject Inventory() => Op(
        "Inventory",
        "8337eb8541b314040b0edde0c09c5c7a2783ba1960aa9edfbf3bac16d0fec404",
        new JsonObject { ["fetchRewardCampaigns"] = false });

    /// <summary>Текущий прогресс дропа на канале.</summary>
    public static JsonObject CurrentDrop(long channelId) => Op(
        "DropCurrentSessionContext",
        "4d06b702d25d652afb9ef835d2a550031f1cf762b193523a92166f40ea3d142b",
        new JsonObject { ["channelID"] = channelId.ToString(), ["channelLogin"] = "" });

    /// <summary>Все доступные кампании.</summary>
    public static JsonObject Campaigns() => Op(
        "ViewerDropsDashboard",
        "c16bb890cc8ce7647a96ee69cd313d423a378a3dedadf630a1017cde18975feb",
        new JsonObject { ["fetchRewardCampaigns"] = false });

    /// <summary>Подробности кампании.</summary>
    public static JsonObject CampaignDetails(string userLogin, string campaignId) => Op(
        "DropCampaignDetails",
        "039277bf98f3130929262cc7c6efd9c141ca3749cb6dca442fc8ead9a53f77c1",
        new JsonObject { ["channelLogin"] = userLogin, ["dropID"] = campaignId });

    /// <summary>Дропы, доступные на канале.</summary>
    public static JsonObject AvailableDrops(long channelId) => Op(
        "DropsHighlightService_AvailableDrops",
        "782dad0f032942260171d2d80a654f88bdd0c5a9dddc392e9bc92218a0f42d20",
        new JsonObject { ["channelID"] = channelId.ToString() });

    /// <summary>Живые каналы по игре.</summary>
    public static JsonObject GameDirectory(string slug, int limit, bool dropsEnabled) => Op(
        "DirectoryPage_Game",
        "86bcceb4e8b1a51256ff8eed8bd8aae4acacf80d737efe904f84f3aeadf8cafd",
        new JsonObject
        {
            ["limit"] = limit,
            ["slug"] = slug,
            ["includeIsDJ"] = false,
            ["imageWidth"] = 50,
            ["includeCostreaming"] = false,
            ["options"] = new JsonObject
            {
                ["broadcasterLanguages"] = new JsonArray(),
                ["freeformTags"] = null,
                ["includeRestricted"] = new JsonArray("SUB_ONLY_LIVE"),
                ["recommendationsContext"] = new JsonObject { ["platform"] = "web" },
                ["sort"] = "RELEVANCE",
                ["systemFilters"] = dropsEnabled ? new JsonArray("DROPS_ENABLED") : new JsonArray(),
                ["tags"] = new JsonArray(),
                ["requestID"] = "JIRA-VXP-2397",
            },
            ["sortTypeIsRecency"] = false,
        });

    /// <summary>Удалить уведомление на сайте.</summary>
    public static JsonObject NotificationsDelete(string id) => Op(
        "OnsiteNotifications_DeleteNotification",
        "13d463c831f28ffe17dccf55b3148ed8b3edbbd0ebadd56352f1ff0160616816",
        new JsonObject { ["input"] = new JsonObject { ["id"] = id } });
}
