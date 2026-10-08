using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using TwitchDropsMiner.Core;

namespace TwitchDropsMiner.UI;

/// <summary>
/// Состояние главного окна. Реализует IMinerUi — через него ядро обновляет интерфейс.
/// </summary>
public sealed class MainViewModel : ObservableObject, IMinerUi
{
    private const int MaxLogLines = 3000;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    public Settings Settings { get; }
    public Miner Miner { get; }
    public TrayIcon Tray { get; }

    /// <summary>Окно задаёт действия, которые требуют доступа к нему.</summary>
    public Action? ShowWindowAction { get; set; }
    public Action? MinimizeToTrayAction { get; set; }

    public MainViewModel(Settings settings, TrayIcon tray)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = settings;
        Tray = tray;
        for (int i = 0; i < Limits.MaxWebsockets; i++)
            Websockets.Add(new WebsocketRow { Name = L.F("gui.websocket.websocket", "Websocket #{id}:", ("id", i + 1)) });
        Miner = new Miner(settings, this);

        CampaignsView = CollectionViewSource.GetDefaultView(Campaigns);
        CampaignsView.Filter = o => o is CampaignView cv && Filter.Matches(cv.Campaign, Settings);
        Filter.NotLinked = settings.PriorityMode == PriorityMode.PriorityOnly;
        Filter.Changed += RefreshInventory;
        MainCampaignsView = new ListCollectionView(Campaigns)
        {
            Filter = o => o is CampaignView cv && (cv.IsCurrent || InventoryFilter.MatchesMain(cv.Campaign, Settings)),
        };
        MainCampaignsView.SortDescriptions.Add(new SortDescription(nameof(CampaignView.IsCurrent), ListSortDirection.Descending));

        PriorityList = new ObservableCollection<string>(settings.Priority);
        ExcludeList = new ObservableCollection<string>(settings.Exclude);
        _autostart = SafeQueryAutostart();

        SwitchCommand = new RelayCommand(() => Miner.ChangeState(MinerState.ChannelSwitch), _ => SelectedChannel is not null);
        ReloadCommand = new RelayCommand(() => Miner.ChangeState(MinerState.InventoryFetch));
        LogoutCommand = new RelayCommand(async () => { LogoutEnabled = false; await Miner.LogoutAsync(); }, _ => LogoutEnabled);
        MinimizeCommand = new RelayCommand(() => MinimizeToTrayAction?.Invoke());
        OpenActivationCommand = new RelayCommand(() => Util.OpenUrl(DeviceCodeUrl ?? "https://www.twitch.tv/activate"));
        CopyCodeCommand = new RelayCommand(() => { if (DeviceCode is not null) TrySetClipboard(DeviceCode); });
        OpenLinkCommand = new RelayCommand(p => { if (p is string url && url.Length > 0) Util.OpenUrl(url); });
        RefreshInventoryCommand = new RelayCommand(RefreshInventory);
        OpenDataFolderCommand = new RelayCommand(() => Util.OpenUrl(AppPaths.DataDir));
        ImportLegacyCommand = new RelayCommand(ImportLegacy);
        BrowserLoginCommand = new RelayCommand(async () => await BrowserLoginAsync());
        ClearLogCommand = new RelayCommand(() => LogLines.Clear());
        CopyLogCommand = new RelayCommand(p =>
        {
            var text = p is System.Collections.IList { Count: > 0 } sel
                ? string.Join(Environment.NewLine, sel.Cast<string>())
                : string.Join(Environment.NewLine, LogLines);
            TrySetClipboard(text);
        });
        PriorityAddCommand = new RelayCommand(PriorityAdd);
        PriorityMoveCommand = new RelayCommand(p => PriorityMove(Convert.ToInt32(p)));
        PriorityDeleteCommand = new RelayCommand(PriorityDelete, _ => SelectedPriority is not null);
        ExcludeAddCommand = new RelayCommand(ExcludeAdd);
        ExcludeDeleteCommand = new RelayCommand(ExcludeDelete, _ => SelectedExclude is not null);

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateRemaining(), _dispatcher);
        _timer.Start();
        DisplayDrop(null);
        SetLoginStatus(L.T("gui.login.logged_out", "Logged out"), null);
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch { /* буфер обмена занят другим процессом */ }
    }

    private static bool SafeQueryAutostart()
    {
        try { return Autostart.IsEnabled(); } catch { return false; }
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    #region Главная вкладка

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public ObservableCollection<WebsocketRow> Websockets { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    private string _loginStatus = "";
    public string LoginStatus { get => _loginStatus; set => Set(ref _loginStatus, value); }
    private string _userIdText = "-";
    public string UserIdText { get => _userIdText; set => Set(ref _userIdText, value); }
    private string? _deviceCode;
    public string? DeviceCode { get => _deviceCode; set => Set(ref _deviceCode, value); }
    private string? _deviceCodeUrl;
    public string? DeviceCodeUrl { get => _deviceCodeUrl; set => Set(ref _deviceCodeUrl, value); }
    private bool _logoutEnabled;
    public bool LogoutEnabled { get => _logoutEnabled; set { if (Set(ref _logoutEnabled, value)) CommandManager_Invalidate(); } }

    // Прогресс
    private string _gameName = "...", _campaignName = "...", _campaignPercent = "-%", _campaignRemaining = "";
    private string _dropRewards = "...", _dropPercent = "-%", _dropRemaining = "";
    private double _campaignProgress, _dropProgress;
    public string GameName { get => _gameName; set => Set(ref _gameName, value); }
    public string CampaignName { get => _campaignName; set => Set(ref _campaignName, value); }
    public string CampaignPercent { get => _campaignPercent; set => Set(ref _campaignPercent, value); }
    public string CampaignRemaining { get => _campaignRemaining; set => Set(ref _campaignRemaining, value); }
    public double CampaignProgress { get => _campaignProgress; set => Set(ref _campaignProgress, value); }
    public string DropRewards { get => _dropRewards; set => Set(ref _dropRewards, value); }
    public string DropPercent { get => _dropPercent; set => Set(ref _dropPercent, value); }
    public string DropRemaining { get => _dropRemaining; set => Set(ref _dropRemaining, value); }
    public double DropProgress { get => _dropProgress; set => Set(ref _dropProgress, value); }

    // Каналы
    public ObservableCollection<ChannelRow> Channels { get; } = [];
    private readonly Dictionary<long, ChannelRow> _channelRows = new();
    private ChannelRow? _selectedChannel;
    public ChannelRow? SelectedChannel { get => _selectedChannel; set { if (Set(ref _selectedChannel, value)) CommandManager_Invalidate(); } }

    public RelayCommand SwitchCommand { get; }
    public RelayCommand MinimizeCommand { get; }
    public RelayCommand OpenActivationCommand { get; }
    public RelayCommand CopyCodeCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CopyLogCommand { get; }

    private static void CommandManager_Invalidate() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    #endregion

    #region IMinerUi

    public void Print(string message) => OnUi(() =>
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        foreach (var line in message.Replace("\r", "").Split('\n'))
            LogLines.Add($"{stamp}: {line}");
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        LogAppended?.Invoke();
    });

    public event Action? LogAppended;

    public void SetStatus(string text) => OnUi(() => Status = text);

    public void SetWebsocketStatus(int index, string? status, int? topics) => OnUi(() =>
    {
        if (index < 0 || index >= Websockets.Count) return;
        var row = Websockets[index];
        if (status is not null) row.Status = status;
        if (topics is not null) row.Topics = $"{topics,3}/{Limits.WsTopicsLimit}";
        else if (row.Topics.Length == 0) row.Topics = $"{0,3}/{Limits.WsTopicsLimit}";
    });

    public void RemoveWebsocket(int index) => OnUi(() =>
    {
        if (index < 0 || index >= Websockets.Count) return;
        Websockets[index].Status = "";
        Websockets[index].Topics = "";
    });

    public void SetLoginStatus(string status, long? userId) => OnUi(() =>
    {
        LoginStatus = status;
        UserIdText = userId?.ToString() ?? "-";
    });

    public void ShowDeviceCode(string? userCode, string? verificationUrl) => OnUi(() =>
    {
        DeviceCode = userCode;
        DeviceCodeUrl = verificationUrl;
    });

    public void SetLogoutEnabled(bool enabled) => OnUi(() => LogoutEnabled = enabled);

    public void GrabAttention() => OnUi(() => ShowWindowAction?.Invoke());

    public void ChannelsClear()
    {
        Channels.Clear();
        _channelRows.Clear();
        SelectedChannel = null;
    }

    public void ChannelDisplay(Channel channel, bool add)
    {
        if (!_channelRows.TryGetValue(channel.Id, out var row))
        {
            if (!add) return;
            row = new ChannelRow { Id = channel.Id };
            _channelRows[channel.Id] = row;
            Channels.Add(row);
        }
        row.Name = channel.Name;
        (row.Status, row.StatusKind) = channel.Online
            ? (L.T("gui.channels.online", "ONLINE  ✔"), StatusKind.Good)
            : channel.PendingOnline
                ? (L.T("gui.channels.pending", "OFFLINE ⏳"), StatusKind.Warn)
                : (L.T("gui.channels.offline", "OFFLINE ❌"), StatusKind.Bad);
        row.Game = channel.Game?.Name ?? "";
        row.Drops = channel.DropsEnabled ? "✔" : "❌";
        row.Viewers = channel.Viewers;
        row.Acl = channel.AclBased ? "✔" : "❌";
    }

    public void ChannelRemove(Channel channel)
    {
        if (_channelRows.Remove(channel.Id, out var row))
        {
            if (SelectedChannel == row) SelectedChannel = null;
            Channels.Remove(row);
        }
    }

    public void SetWatching(Channel? channel)
    {
        foreach (var row in Channels) row.IsWatching = channel is not null && row.Id == channel.Id;
    }

    public long? SelectedChannelId => SelectedChannel?.Id;

    public void DisplayDrop(TimedDrop? drop)
    {
        Tray.UpdateTitle(drop);
        MarkCurrent(drop);
        if (drop is null)
        {
            DropRewards = "...";
            DropProgress = 0;
            DropPercent = "-%";
            CampaignName = "...";
            GameName = "...";
            CampaignProgress = 0;
            CampaignPercent = "-%";
        }
        else
        {
            var c = drop.Campaign;
            DropRewards = drop.RewardsText();
            DropProgress = drop.Progress;
            DropPercent = drop.Progress.ToString("P1");
            CampaignName = c.Name;
            GameName = c.Game.Name;
            CampaignProgress = c.Progress;
            CampaignPercent = $"{c.Progress:P1} ({c.ClaimedDrops}/{c.TotalDrops})";
        }
        UpdateRemaining();
    }

    private void UpdateRemaining()
    {
        var p = Miner.Progress;
        var drop = p.Drop;
        int dropMinutes = drop?.RemainingMinutes ?? 0;
        int campaignMinutes = drop?.Campaign.RemainingMinutes ?? 0;
        DropRemaining = L.F("gui.progress.remaining", "{time} remaining", ("time", p.FormatRemaining(dropMinutes)));
        CampaignRemaining = L.F("gui.progress.remaining", "{time} remaining", ("time", p.FormatRemaining(campaignMinutes)));
    }

    public void InventoryClear()
    {
        _dropViews.Clear();
        Campaigns.ReplaceAll([]);
    }

    public Task InventoryAddCampaignAsync(DropsCampaign campaign)
    {
        var drops = campaign.Drops.Select(d => new DropView
        {
            Drop = d,
            Benefits = d.Benefits.Select(b => new BenefitView { Name = b.Name }).ToList(),
        }).ToList();
        var view = new CampaignView { Campaign = campaign, Drops = drops };
        view.RefreshStatus();
        foreach (var dv in drops)
        {
            dv.Refresh();
            _dropViews[dv.Drop.Id] = dv;
        }
        Campaigns.Add(view);
        _ = LoadImagesAsync(view);
        return Task.CompletedTask;
    }

    private async Task LoadImagesAsync(CampaignView view)
    {
        try
        {
            view.Image = await ImageLoader.LoadAsync(Miner.Images, view.Campaign.ImageUrl, 108);
            foreach (var dv in view.Drops)
            {
                for (int i = 0; i < dv.Benefits.Count; i++)
                    dv.Benefits[i].Image = await ImageLoader.LoadAsync(Miner.Images, dv.Drop.Benefits[i].ImageUrl, 80);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Image load: {ex.Message}");
        }
    }

    public void InventoryUpdateDrop(TimedDrop drop)
    {
        if (_dropViews.TryGetValue(drop.Id, out var dv)) dv.Refresh();
    }

    public void SetGames(IEnumerable<Game> games)
    {
        foreach (var g in games) _gameNames.Add(g.Name);
        UpdateChoices();
    }

    public void SetTrayIcon(TrayIconState state) => OnUi(() => Tray.SetState(state));

    public void Notify(string title, string message) => OnUi(() => Tray.Notify(title, message));

    #endregion

    #region Инвентарь

    public ObservableList<CampaignView> Campaigns { get; } = new();
    public ListCollectionView MainCampaignsView { get; }
    private string? _currentDropId;

    /// <summary>Подсветка кампании и дропа, которые сейчас добываются.</summary>
    private void MarkCurrent(TimedDrop? drop)
    {
        var id = drop?.Id;
        bool changed = id != _currentDropId;
        _currentDropId = id;
        foreach (var c in Campaigns)
        {
            bool campaignCurrent = drop is not null && c.Campaign == drop.Campaign;
            if (c.IsCurrent != campaignCurrent) { c.IsCurrent = campaignCurrent; changed = true; }
            foreach (var d in c.Drops) d.IsCurrent = d.Drop.Id == id;
        }
        if (changed) MainCampaignsView.Refresh();
    }
    public ICollectionView CampaignsView { get; }
    public InventoryFilter Filter { get; } = new();
    private readonly Dictionary<string, DropView> _dropViews = new();
    public RelayCommand RefreshInventoryCommand { get; }
    public RelayCommand OpenLinkCommand { get; }

    public void RefreshInventory()
    {
        foreach (var c in Campaigns)
        {
            c.RefreshStatus();
            foreach (var d in c.Drops) d.Refresh();
        }
        CampaignsView.Refresh();
        MainCampaignsView.Refresh();
    }

    #endregion

    #region Настройки

    public IReadOnlyList<string> Languages => L.Languages;
    public string Language
    {
        get => L.Current;
        set
        {
            if (value == Settings.Language) return;
            Settings.Language = value;
            SaveSettings();
            Print(L.T("x.settings.restart_needed", "The change will take effect after a restart."));
        }
    }

    public string[] ThemeNames { get; } =
    [
        L.T("x.settings.theme.system", "System"),
        L.T("x.settings.theme.light", "Light"),
        L.T("x.settings.theme.dark", "Dark"),
    ];
    public int ThemeIndex
    {
        get => (int)Settings.Theme;
        set
        {
            Settings.Theme = (AppTheme)value;
            SaveSettings();
            App.ApplyTheme(Settings.Theme);
            Raise();
        }
    }

    public string[] PriorityModeNames { get; } =
    [
        L.T("gui.settings.priority_modes.priority_only", "Priority list only"),
        L.T("gui.settings.priority_modes.ending_soonest", "Ending soonest"),
        L.T("gui.settings.priority_modes.low_availability", "Low availability first"),
    ];
    public int PriorityModeIndex
    {
        get => (int)Settings.PriorityMode;
        set { Settings.PriorityMode = (PriorityMode)value; SaveSettings(); Raise(); ScheduleApply(); }
    }

    private bool _autostart;
    public bool AutostartEnabled
    {
        get => _autostart;
        set { if (Set(ref _autostart, value)) ApplyAutostart(); }
    }
    public bool AutostartTray
    {
        get => Settings.AutostartTray;
        set { Settings.AutostartTray = value; SaveSettings(); ApplyAutostart(); Raise(); }
    }

    private void ApplyAutostart()
    {
        try { Autostart.Set(_autostart, Settings.AutostartTray, Settings.ArgVerbose); }
        catch (Exception ex) { Print($"Autostart: {ex.Message}"); }
    }

    public bool TrayNotifications
    {
        get => Settings.TrayNotifications;
        set { Settings.TrayNotifications = value; SaveSettings(); Raise(); }
    }
    public bool CloseToTray
    {
        get => Settings.CloseToTray;
        set { Settings.CloseToTray = value; SaveSettings(); Raise(); }
    }
    public bool EnableBadgesEmotes
    {
        get => Settings.EnableBadgesEmotes;
        set { Settings.EnableBadgesEmotes = value; SaveSettings(); Raise(); }
    }
    public bool AvailableDropsCheck
    {
        get => Settings.AvailableDropsCheck;
        set { Settings.AvailableDropsCheck = value; SaveSettings(); Raise(); }
    }
    public bool BrowserPlayer
    {
        get => Settings.BrowserPlayer;
        set { Settings.BrowserPlayer = value; SaveSettings(); Raise(); ScheduleApply(); }
    }
    public bool UseCampaignCatalog
    {
        get => Settings.UseCampaignCatalog;
        set { Settings.UseCampaignCatalog = value; SaveSettings(); Raise(); }
    }
    public int[] ConnectionQualities { get; } = [1, 2, 3, 4, 5, 6];
    public int ConnectionQuality
    {
        get => Settings.ConnectionQuality;
        set
        {
            Settings.ConnectionQuality = value;
            SaveSettings();
            Print(L.T("x.settings.restart_needed", "The change will take effect after a restart."));
            Raise();
        }
    }

    private string _proxy = "";
    public string Proxy
    {
        get => string.IsNullOrEmpty(_proxy) ? Settings.Proxy : _proxy;
        set => Set(ref _proxy, value);
    }

    /// <summary>Проверка прокси при потере фокуса (как в оригинале — неверное значение сбрасывается).</summary>
    public void CommitProxy()
    {
        var value = (_proxy ?? "").Trim();
        if (value == "http://") value = "";
        if (!HttpService.IsValidProxy(value)) value = "";
        _proxy = value;
        Raise(nameof(Proxy));
        if (Settings.Proxy == value) return;
        Settings.Proxy = value;
        SaveSettings();
        Print(L.T("x.settings.restart_needed", "The change will take effect after a restart."));
    }

    public RelayCommand ImportLegacyCommand { get; }
    public RelayCommand BrowserLoginCommand { get; }

    private bool _browserBusy;

    /// <summary>Вход через встроенный браузер: пользователь сам входит на twitch.tv.</summary>
    private async Task BrowserLoginAsync()
    {
        if (_browserBusy) return;
        if (!TwitchBrowser.IsRuntimeAvailable())
        {
            Print(L.T("x.browser.no_runtime", "Microsoft Edge WebView2 Runtime is not installed: https://go.microsoft.com/fwlink/p/?LinkId=2124703"));
            return;
        }
        _browserBusy = true;
        try
        {
            Print(L.T("x.browser.opened", "Log in to Twitch in the opened window."));
            var result = await TwitchBrowser.LoginAsync(Application.Current.MainWindow);
            if (result is not { } r)
            {
                Print(L.T("x.browser.cancelled", "Browser login was cancelled."));
                return;
            }
            Print(L.T("x.browser.ok", "Browser login succeeded, restarting..."));
            Miner.Auth.SubmitBrowserLogin(r.token, r.deviceId, r.userAgent);
        }
        catch (Exception ex)
        {
            Print($"Browser login error: {ex.Message}");
        }
        finally { _browserBusy = false; }
    }

    public void BrowserWatch(string? login) => OnUi(async () =>
    {
        try { await TwitchBrowser.WatchAsync(login); }
        catch (Exception ex) { Log.Warning($"Browser player failed: {ex.Message}"); }
    });

    public async Task<string?> BrowserPlayerStateAsync() => await TwitchBrowser.PlayerStateAsync();

    public async Task<System.Text.Json.Nodes.JsonNode?> FetchSiteInventoryAsync(CancellationToken ct)
    {
        try { return await TwitchBrowser.FetchSiteInventoryAsync(ct); }
        catch (Exception ex)
        {
            Log.Warning($"Site inventory failed: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> GetIntegrityTokenAsync(CancellationToken ct)
    {
        try { return await TwitchBrowser.GetIntegrityTokenAsync(ct); }
        catch (Exception ex)
        {
            Log.Warning($"Integrity from browser failed: {ex.Message}");
            return null;
        }
    }

    private void ImportLegacy()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("x.import.button", "Import login from the Python version"),
            Filter = "cookies.jar|cookies.jar|*.*|*.*",
            FileName = "cookies.jar",
        };
        if (dlg.ShowDialog() == true) _ = Miner.ImportLegacyLoginAsync(dlg.FileName);
    }

    public string DataDir => AppPaths.DataDir;
    public RelayCommand OpenDataFolderCommand { get; }

    private void SaveSettings()
    {
        try { Settings.Save(); }
        catch (Exception ex) { Print($"Settings save error: {ex.Message}"); }
    }

    private DispatcherTimer? _applyTimer;

    /// <summary>
    /// Изменения приоритета/исключений/режима применяются автоматически через 2 с
    /// (в оригинале требовалась кнопка «Перезагрузить»).
    /// </summary>
    private void ScheduleApply()
    {
        RefreshInventory();
        _applyTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _applyTimer!.Stop();
            if (Miner.Auth.IsLoggedIn && Miner.Inventory.Count > 0 && Miner.State != MinerState.Exit)
                Miner.ChangeState(MinerState.GamesUpdate);
        }, _dispatcher);
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    // Списки приоритета и исключений
    private readonly SortedSet<string> _gameNames = new(StringComparer.CurrentCultureIgnoreCase);
    public ObservableCollection<string> PriorityList { get; }
    public ObservableCollection<string> ExcludeList { get; }
    public ObservableList<string> PriorityChoices { get; } = new();
    public ObservableList<string> ExcludeChoices { get; } = new();

    private string _priorityInput = "";
    public string PriorityInput { get => _priorityInput; set => Set(ref _priorityInput, value); }
    private string _excludeInput = "";
    public string ExcludeInput { get => _excludeInput; set => Set(ref _excludeInput, value); }
    private string? _selectedPriority;
    public string? SelectedPriority { get => _selectedPriority; set { if (Set(ref _selectedPriority, value)) CommandManager_Invalidate(); } }
    private string? _selectedExclude;
    public string? SelectedExclude { get => _selectedExclude; set { if (Set(ref _selectedExclude, value)) CommandManager_Invalidate(); } }

    public RelayCommand PriorityAddCommand { get; }
    public RelayCommand PriorityMoveCommand { get; }
    public RelayCommand PriorityDeleteCommand { get; }
    public RelayCommand ExcludeAddCommand { get; }
    public RelayCommand ExcludeDeleteCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand LogoutCommand { get; }

    private void UpdateChoices()
    {
        PriorityChoices.ReplaceAll(_gameNames.Where(n => !Settings.Priority.Contains(n)));
        ExcludeChoices.ReplaceAll(_gameNames.Where(n => !Settings.Exclude.Contains(n)));
    }

    private void PriorityAdd()
    {
        var name = PriorityInput.Trim();
        if (name.Length == 0) return;
        PriorityInput = "";
        if (Settings.Priority.Contains(name)) { SelectedPriority = name; return; }
        Settings.Priority.Add(name);
        PriorityList.Add(name);
        SelectedPriority = name;
        SaveSettings();
        UpdateChoices();
        ScheduleApply();
    }

    /// <summary>amount &gt; 0 — вверх, &lt; 0 — вниз; int.MaxValue — в начало, -int.MaxValue — в конец.</summary>
    private void PriorityMove(int amount)
    {
        if (SelectedPriority is not { } item) return;
        int idx = PriorityList.IndexOf(item);
        int max = PriorityList.Count - 1;
        if (idx < 0 || amount == 0 || (amount > 0 && idx == 0) || (amount < 0 && idx == max)) return;
        long target = Math.Clamp((long)idx - amount, 0, max);
        PriorityList.Move(idx, (int)target);
        Settings.Priority.RemoveAt(idx);
        Settings.Priority.Insert((int)target, item);
        SelectedPriority = item;
        SaveSettings();
        ScheduleApply();
    }

    private void PriorityDelete()
    {
        if (SelectedPriority is not { } item) return;
        PriorityList.Remove(item);
        Settings.Priority.Remove(item);
        SelectedPriority = null;
        SaveSettings();
        UpdateChoices();
        ScheduleApply();
    }

    private void ExcludeAdd()
    {
        var name = ExcludeInput.Trim();
        if (name.Length == 0) return;
        ExcludeInput = "";
        if (Settings.Exclude.Add(name))
        {
            int i = 0;
            while (i < ExcludeList.Count && string.CompareOrdinal(ExcludeList[i], name) < 0) i++;
            ExcludeList.Insert(i, name);
            SaveSettings();
            UpdateChoices();
            ScheduleApply();
        }
        SelectedExclude = name;
    }

    private void ExcludeDelete()
    {
        if (SelectedExclude is not { } item) return;
        ExcludeList.Remove(item);
        Settings.Exclude.Remove(item);
        SelectedExclude = null;
        SaveSettings();
        UpdateChoices();
        ScheduleApply();
    }

    #endregion
}
