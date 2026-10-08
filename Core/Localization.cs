using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TwitchDropsMiner.Core;

/// <summary>
/// Локализация. Использует JSON-файлы переводов оригинального проекта (встроены в exe),
/// плюс собственные строки для новых элементов интерфейса.
/// Порядок поиска: Overrides[язык] → файл языка → English.json → строка по умолчанию из кода.
/// </summary>
public static class L
{
    public const string DefaultLanguage = "English";
    private static readonly Dictionary<string, string> _current = new();
    private static readonly Dictionary<string, string> _english = new();

    public static string Current { get; private set; } = DefaultLanguage;
    public static IReadOnlyList<string> Languages { get; } = LoadLanguageList();

    /// <summary>Исправленные/новые русские строки (перекрывают файл перевода).</summary>
    private static readonly Dictionary<string, string> RussianOverrides = new()
    {
        ["status.goes_online"] = "{channel} в сети — переключаемся...",
        ["status.goes_offline"] = "{channel} вышел из сети — переключаемся...",
        ["status.claimed_drop"] = "Получен дроп: {drop}",
        ["status.watching"] = "Просмотр: {channel}",
        ["status.no_channel"] = "Нет доступных каналов. Ожидание канала в сети...",
        ["status.no_campaign"] = "Нет активных кампаний. Ожидание активной кампании...",
        ["gui.status.idle"] = "Ожидание",
        ["gui.output"] = "Журнал",
        ["gui.tray.notification_title"] = "Дроп получен",
        ["gui.channels.switch"] = "Переключиться",
        ["gui.inventory.filter.not_linked"] = "Не привязанные",
        ["gui.inventory.status.linked"] = "Привязан ✔",
        ["gui.inventory.status.not_linked"] = "Не привязан ❌",
        ["gui.inventory.status.active"] = "Активна ✔",
        ["gui.inventory.status.upcoming"] = "Скоро ⏳",
        ["gui.inventory.status.expired"] = "Завершена ❌",
        ["gui.settings.general.tray"] = "Автозапуск в трей: ",
        ["gui.settings.game_name"] = "Название игры",
        ["gui.settings.reload"] = "Перезагрузить",
        ["gui.help.invalidate.text"] = "Выйти из аккаунта (отозвать токен):",
        ["gui.help.invalidate.button"] = "Выйти",
        // Новые строки .NET-версии
        ["x.login.code_title"] = "Код активации:",
        ["x.login.open_page"] = "Открыть страницу активации",
        ["x.login.copy_code"] = "Скопировать код",
        ["x.login.code_hint"] = "Откройте страницу активации Twitch, войдите в аккаунт и введите этот код.",
        ["x.login.code_print"] = "Введите код на странице активации Twitch: {code}",
        ["x.settings.theme"] = "Тема оформления: ",
        ["x.settings.theme.system"] = "Как в системе",
        ["x.settings.theme.light"] = "Светлая",
        ["x.settings.theme.dark"] = "Тёмная",
        ["x.settings.close_to_tray"] = "Закрытие окна сворачивает в трей: ",
        ["x.settings.connection_quality"] = "Качество соединения (1 — хорошее, 6 — плохое): ",
        ["x.settings.language"] = "Язык (нужен перезапуск): ",
        ["x.settings.restart_needed"] = "Изменение вступит в силу после перезапуска.",
        ["x.channels.hint"] = "Выделите канал и нажмите «Переключиться», чтобы смотреть его вручную.",
        ["x.tray.tooltip_idle"] = "Twitch Drops Miner — ожидание",
        ["x.menu.copy"] = "Копировать",
        ["x.menu.clear"] = "Очистить",
        ["x.data_dir"] = "Папка с данными:",
        ["x.open_folder"] = "Открыть",
        ["x.already_running"] = "Приложение уже запущено — открываю существующее окно.",
        ["x.fatal"] = "Критическая ошибка:",
        ["x.help.how_it_works"] =
            "Приложение выбирает канал с нужной кампанией и «смотрит» его настоящим плеером Twitch в скрытом окне " +
            "встроенного браузера — без звука и в качестве 160p. С осени 2026 Twitch засчитывает время только так. " +
            "Дополнительно раз в минуту отправляется событие «минута просмотра». Реальный прогресс раз в 5 минут " +
            "берётся со страницы инвентаря Twitch, между проверками он показывается оценочно. Статусы каналов приходят " +
            "по websocket-соединениям Twitch PubSub. Готовые награды забираются автоматически, раз в час список кампаний обновляется.",
        ["x.help.getting_started"] =
            "1. При первом запуске откроется окно входа — войдите в Twitch как обычно (кнопка «Войти через браузер» открывает его снова).\n" +
            "2. Убедитесь, что аккаунт Twitch привязан к нужным играм (ссылка «Все кампании» выше).\n" +
            "3. На вкладке «Настройки» добавьте игры в список «Приоритет» — майнинг начнётся через пару секунд.\n" +
            "4. Режим «Только список приоритета» добывает только игры из списка. Чтобы добывать всё подряд, " +
            "выберите «Сначала заканчивающиеся» или «Сначала с малым запасом времени».\n" +
            "5. Список «Исключения» — игры, которые не добываются никогда.\n" +
            "6. Не смотрите стримы тем же аккаунтом в обычном браузере, пока работает майнер.",
        ["x.diag.line"] = "Плеер: {player} · реальный прогресс: {sync}",
        ["x.diag.hint"] = "Состояние скрытого плеера Twitch и время последней синхронизации реального прогресса со страницы инвентаря Twitch (раз в 5 минут).",
        ["x.diag.playing"] = "играет",
        ["x.diag.restarting"] = "перезапуск",
        ["x.diag.off"] = "выключен",
        ["x.diag.problem"] = "не играет",
        ["x.settings.catalog"] = "Брать список кампаний из публичного каталога, если Twitch его не отдаёт",
        ["x.settings.catalog_hint"] =
            "С сентября 2026 Twitch не отдаёт список кампаний клиентам, которые входят по коду.\n" +
            "Тогда список берётся с twitch-drops-api.sunkwi.com (без передачи токена).\n" +
            "Прогресс, просмотр и получение наград по-прежнему идут напрямую через Twitch.",
        ["x.catalog_failed"] = "Twitch не отдал список кампаний, а публичный каталог недоступен.",
        ["x.main.campaigns"] = "Кампании и дропы",
        ["x.settings.player"] = "Смотреть стрим встроенным плеером (нужен вход через браузер)",
        ["x.settings.player_hint"] =
            "С осени 2026 Twitch засчитывает время просмотра только при работе настоящего плеера.\n" +
            "Стрим открывается в скрытом окне без звука в качестве 160p (~600 МБ памяти, ~1-2 % CPU).\n" +
            "Реальный прогресс раз в 5 минут берётся со страницы инвентаря Twitch.",
        ["x.player_required"] = "Twitch засчитывает просмотр только через свой плеер: войдите через браузер (кнопка «Войти через браузер»), иначе прогресс не пойдёт.",
        ["x.browser.button"] = "Войти через браузер",
        ["x.login.browser_request"] = "Войдите в Twitch в открывшемся окне (кнопка «Войти через браузер»).",
        ["x.browser.hint"] =
            "Откроется окно twitch.tv (встроенный Microsoft Edge). Войдите как обычно —\n" +
            "логин, пароль и 2FA вводятся только на сайте Twitch, приложение их не видит.",
        ["x.browser.title"] = "Вход в Twitch",
        ["x.browser.opened"] = "Войдите в Twitch в открывшемся окне.",
        ["x.browser.cancelled"] = "Вход через браузер отменён.",
        ["x.browser.ok"] = "Вход через браузер выполнен, перезапуск...",
        ["x.browser.no_runtime"] = "Не установлен Microsoft Edge WebView2 Runtime: https://go.microsoft.com/fwlink/p/?LinkId=2124703",
        ["x.import.button"] = "Импорт из Python-версии…",
        ["x.import.hint"] =
            "Выберите cookies.jar старой версии TwitchDropsMiner.\n" +
            "Токен клиента Android-приложения даёт полный доступ к кампаниям и прогрессу,\n" +
            "а новый вход по коду сейчас возможен только через клиент Android TV с ограничениями.",
        ["x.import.no_token"] = "В этом файле не найден auth-token.",
        ["x.import.invalid"] = "Токен из файла недействителен (HTTP {status}).",
        ["x.import.unknown_client"] = "Неизвестный клиент Twitch: {client}",
        ["x.import.ok"] = "Вход импортирован (user ID {user}), перезапуск...",
        ["x.progress_estimated"] = "Twitch не сообщает прогресс этому клиенту — показан оценочный прогресс.",
        ["gui.settings.priority_modes.priority_only"] = "Только список приоритета",
        ["gui.settings.priority_modes.ending_soonest"] = "Сначала заканчивающиеся",
        ["gui.settings.priority_modes.low_availability"] = "Сначала с малым запасом времени",
    };

    private static readonly Dictionary<string, string> EnglishExtras = new()
    {
        ["x.login.code_title"] = "Activation code:",
        ["x.login.open_page"] = "Open activation page",
        ["x.login.copy_code"] = "Copy code",
        ["x.login.code_hint"] = "Open the Twitch activation page, log in and enter this code.",
        ["x.login.code_print"] = "Enter this code on the Twitch device activation page: {code}",
        ["x.settings.theme"] = "Theme: ",
        ["x.settings.theme.system"] = "System",
        ["x.settings.theme.light"] = "Light",
        ["x.settings.theme.dark"] = "Dark",
        ["x.settings.close_to_tray"] = "Closing the window minimizes to tray: ",
        ["x.settings.connection_quality"] = "Connection quality (1 — good, 6 — poor): ",
        ["x.settings.language"] = "Language (requires restart): ",
        ["x.settings.restart_needed"] = "The change will take effect after a restart.",
        ["x.channels.hint"] = "Select a channel and press \"Switch\" to watch it manually.",
        ["x.tray.tooltip_idle"] = "Twitch Drops Miner — idle",
        ["x.menu.copy"] = "Copy",
        ["x.menu.clear"] = "Clear",
        ["x.data_dir"] = "Data folder:",
        ["x.open_folder"] = "Open",
        ["x.already_running"] = "The application is already running — activating the existing window.",
        ["x.fatal"] = "Fatal error:",
        ["x.settings.catalog"] = "Use the public campaign catalog when Twitch does not return the campaign list",
        ["x.settings.catalog_hint"] =
            "Since September 2026 Twitch hides the campaign list from clients that log in with a device code.\n" +
            "In that case the list is taken from twitch-drops-api.sunkwi.com (no token is sent there).\n" +
            "Progress, watching and claiming still go directly through Twitch.",
        ["x.catalog_failed"] = "Twitch did not return the campaign list and the public catalog is unavailable.",
        ["x.main.campaigns"] = "Campaigns and drops",
        ["x.settings.player"] = "Watch the stream with the built-in player (requires browser login)",
        ["x.settings.player_hint"] =
            "Since autumn 2026 Twitch counts watch time only while its real player is running.\n" +
            "The stream is opened in a hidden muted window at 160p (~600 MB RAM, ~1-2 % CPU).\n" +
            "Real progress is read from the Twitch inventory page every 5 minutes.",
        ["x.player_required"] = "Twitch counts watch time only through its player: log in with the browser, otherwise progress will not advance.",
        ["x.browser.button"] = "Log in with browser",
        ["x.login.browser_request"] = "Log in to Twitch in the opened window (button \"Log in with browser\").",
        ["x.browser.hint"] =
            "Opens twitch.tv in a built-in Microsoft Edge window. Log in as usual —\n" +
            "login, password and 2FA are entered only on the Twitch site, the app never sees them.",
        ["x.browser.title"] = "Twitch login",
        ["x.browser.opened"] = "Log in to Twitch in the opened window.",
        ["x.browser.cancelled"] = "Browser login was cancelled.",
        ["x.browser.ok"] = "Browser login succeeded, restarting...",
        ["x.browser.no_runtime"] = "Microsoft Edge WebView2 Runtime is not installed: https://go.microsoft.com/fwlink/p/?LinkId=2124703",
        ["x.import.button"] = "Import from Python version…",
        ["x.import.hint"] =
            "Select cookies.jar of the old TwitchDropsMiner.\n" +
            "An Android app client token gives full access to campaigns and progress,\n" +
            "while a new device-code login currently works only with the limited Android TV client.",
        ["x.import.no_token"] = "No auth-token found in this file.",
        ["x.import.invalid"] = "The token from this file is not valid (HTTP {status}).",
        ["x.import.unknown_client"] = "Unknown Twitch client: {client}",
        ["x.import.ok"] = "Login imported (user ID {user}), restarting...",
        ["x.progress_estimated"] = "Twitch does not report progress to this client — estimated progress is shown.",
        ["x.help.how_it_works"] =
            "The application picks a channel with a wanted campaign and watches it with the real Twitch player in a hidden " +
            "built-in browser window (muted, 160p). Since autumn 2026 this is the only way Twitch counts watch time. " +
            "A minute-watched event is also sent every minute. Real progress is read from the Twitch inventory page every " +
            "5 minutes and estimated in between. Channel statuses arrive over Twitch PubSub websockets. Finished drops are " +
            "claimed automatically and the campaign list is refreshed every hour.",
        ["x.help.getting_started"] =
            "1. On first start the login window opens — log in to Twitch as usual (\"Log in with browser\" opens it again).\n" +
            "2. Make sure your Twitch account is linked to the games you want (see the campaigns link above).\n" +
            "3. On the Settings tab add games to the Priority list — mining starts within a couple of seconds.\n" +
            "4. \"Priority list only\" mines only the listed games. To mine everything, choose \"Ending soonest\" " +
            "or \"Low availability first\".\n" +
            "5. The Exclude list contains games that are never mined.\n" +
            "6. Do not watch streams with the same account in a regular browser while the miner is running.",
        ["x.diag.line"] = "Player: {player} · real progress: {sync}",
        ["x.diag.hint"] = "State of the hidden Twitch player and the time of the last real progress sync from the Twitch inventory page (every 5 minutes).",
        ["x.diag.playing"] = "playing",
        ["x.diag.restarting"] = "restarting",
        ["x.diag.off"] = "off",
        ["x.diag.problem"] = "not playing",
    };

    private static IReadOnlyList<string> LoadLanguageList()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("Lang.") && n.EndsWith(".json"))
            .Select(n => n["Lang.".Length..^".json".Length])
            .OrderBy(n => n == DefaultLanguage ? "" : n, StringComparer.CurrentCulture)
            .ToList();
    }

    private static void LoadInto(string language, Dictionary<string, string> target)
    {
        target.Clear();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Lang.{language}.json");
        if (stream is null) return;
        var root = JsonNode.Parse(stream);
        void Walk(JsonNode? node, string prefix)
        {
            if (node is JsonObject obj)
                foreach (var (k, v) in obj) Walk(v, prefix.Length == 0 ? k : $"{prefix}.{k}");
            else if (node is JsonValue val && val.TryGetValue<string>(out var s))
                target[prefix] = s;
        }
        Walk(root, "");
    }

    /// <summary>Язык по умолчанию — по языку Windows.</summary>
    public static string DetectSystemLanguage() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "ru" => "Русский",
        "uk" => "Українська",
        "de" => "Deutsch",
        "fr" => "Français",
        "es" => "Español",
        "it" => "Italiano",
        "pl" => "Polski",
        "pt" => "Português",
        "tr" => "Türkçe",
        "cs" => "Čeština",
        "ja" => "日本語",
        _ => DefaultLanguage,
    };

    public static void SetLanguage(string? language)
    {
        if (string.IsNullOrEmpty(language) || !Languages.Contains(language))
            language = DetectSystemLanguage();
        if (!Languages.Contains(language)) language = DefaultLanguage;
        Current = language;
        LoadInto(DefaultLanguage, _english);
        if (language == DefaultLanguage) _current.Clear();
        else LoadInto(language, _current);
    }

    public static bool IsRussian => Current == "Русский";

    /// <summary>Перевод строки по ключу. defaultText — английский текст, если ключа нет нигде.</summary>
    public static string T(string key, string? defaultText = null)
    {
        if (IsRussian && RussianOverrides.TryGetValue(key, out var o)) return o;
        if (_current.TryGetValue(key, out var s)) return s;
        if (_english.TryGetValue(key, out s)) return s;
        if (EnglishExtras.TryGetValue(key, out s)) return s;
        return defaultText ?? key;
    }

    /// <summary>Перевод с подстановкой {name} → value.</summary>
    public static string F(string key, string? defaultText, params (string name, object? value)[] args)
    {
        var s = T(key, defaultText);
        foreach (var (name, value) in args) s = s.Replace("{" + name + "}", value?.ToString());
        return s;
    }
}
