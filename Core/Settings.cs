using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TwitchDropsMiner.Core;

public enum AppTheme { System, Light, Dark }

/// <summary>Настройки приложения (config/settings.json).</summary>
public sealed class Settings
{
    public string Proxy { get; set; } = "";
    public string Language { get; set; } = "";
    public AppTheme Theme { get; set; } = AppTheme.System;
    public List<string> Priority { get; set; } = [];
    public SortedSet<string> Exclude { get; set; } = new(StringComparer.Ordinal);
    public PriorityMode PriorityMode { get; set; } = PriorityMode.PriorityOnly;
    public bool AutostartTray { get; set; }
    public bool TrayNotifications { get; set; } = true;
    public bool CloseToTray { get; set; }
    public int ConnectionQuality { get; set; } = 1;
    public bool EnableBadgesEmotes { get; set; }
    public bool AvailableDropsCheck { get; set; }
    /// <summary>Брать список кампаний из публичного каталога, если Twitch его не отдаёт.</summary>
    public bool UseCampaignCatalog { get; set; } = true;
    /// <summary>При веб-входе смотреть стрим настоящим плеером в скрытом окне (без звука, 160p).</summary>
    public bool BrowserPlayer { get; set; } = true;

    // Параметры командной строки (не сохраняются)
    [JsonIgnore] public bool ArgTray { get; set; }
    [JsonIgnore] public bool ArgLog { get; set; }
    [JsonIgnore] public bool ArgDump { get; set; }
    [JsonIgnore] public int ArgVerbose { get; set; }

    [JsonIgnore] public bool StartInTray => ArgTray;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // кириллица без \uXXXX
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static Settings Load()
    {
        AppPaths.EnsureDirs();
        var path = AppPaths.SettingsFile;
        foreach (var candidate in new[] { path + ".new", path })
        {
            if (!File.Exists(candidate)) continue;
            var text = File.ReadAllText(candidate);
            // файл Python-версии лежит по тому же пути — импортируем его
            if (IsLegacy(text) && TryImportLegacy(text) is { } imported) return imported.Normalize();
            try
            {
                var s = JsonSerializer.Deserialize<Settings>(text, JsonOpts);
                if (s is not null) return s.Normalize();
            }
            catch (JsonException ex)
            {
                if (candidate.EndsWith(".new")) { File.Delete(candidate); continue; }
                // повреждённый файл не должен мешать запуску: сохраняем копию и начинаем с чистых настроек
                File.Copy(candidate, candidate + ".bak", overwrite: true);
                Log.Error($"settings.json повреждён ({ex.Message}), копия сохранена в settings.json.bak");
                return new Settings().Normalize();
            }
        }
        // Первый запуск: пробуем импортировать настройки Python-версии, лежащие рядом с exe
        var legacy = AppPaths.LegacySettingsFile;
        if (legacy != path && File.Exists(legacy) && TryImportLegacy(File.ReadAllText(legacy)) is { } fromExeDir)
            return fromExeDir.Normalize();
        return new Settings().Normalize();
    }

    private static bool IsLegacy(string text)
    {
        try
        {
            var root = JsonNode.Parse(text);
            return root is JsonObject o && !o.ContainsKey("theme") && (o.ContainsKey("dark_mode") || o["exclude"] is JsonObject);
        }
        catch (JsonException) { return false; }
    }

    private Settings Normalize()
    {
        ConnectionQuality = Math.Clamp(ConnectionQuality, 1, 6);
        Priority = Priority.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        Exclude = new SortedSet<string>(Exclude.Where(p => !string.IsNullOrWhiteSpace(p)), StringComparer.Ordinal);
        return this;
    }

    public void Save()
    {
        AppPaths.EnsureDirs();
        var path = AppPaths.SettingsFile;
        var tmp = path + ".new";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Импорт settings.json из Python-версии ({"__type": ..., "data": ...}).</summary>
    private static Settings? TryImportLegacy(string text)
    {
        try
        {
            var root = JsonNode.Parse(text);
            if (root is null || root.Has("theme")) return null;  // это уже наш формат
            static JsonNode? Unwrap(JsonNode? n) => n is JsonObject o && o.ContainsKey("__type") ? o["data"] : n;
            var s = new Settings
            {
                Proxy = Unwrap(root["proxy"])?.GetValue<string>() ?? "",
                Language = root.Str("language") ?? "",
                Theme = root.Bool("dark_mode") ? AppTheme.Dark : AppTheme.System,
                Priority = root.Arr("priority").Select(x => x.GetValue<string>()).ToList(),
                Exclude = new SortedSet<string>((Unwrap(root["exclude"]) as JsonArray ?? []).Select(x => x!.GetValue<string>()), StringComparer.Ordinal),
                PriorityMode = (PriorityMode)(Unwrap(root["priority_mode"])?.GetValue<int>() ?? 0),
                AutostartTray = root.Bool("autostart_tray"),
                TrayNotifications = root.At("tray_notifications") is null || root.Bool("tray_notifications"),
                ConnectionQuality = root.Int("connection_quality") is var q and > 0 ? q : 1,
                EnableBadgesEmotes = root.Bool("enable_badges_emotes"),
                AvailableDropsCheck = root.Bool("available_drops_check"),
            };
            Log.Info("Импортированы настройки из Python-версии");
            return s;
        }
        catch (Exception ex)
        {
            Log.Warning($"Не удалось импортировать старые настройки: {ex.Message}");
            return null;
        }
    }
}
