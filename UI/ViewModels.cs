using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TwitchDropsMiner.Core;

namespace TwitchDropsMiner.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> execute;
    private readonly Func<object?, bool>? canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<object?, bool>? canExecute = null) : this(_ => execute(), canExecute) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>Цвет статуса (кисти задаются в ресурсах окна).</summary>
public enum StatusKind { Normal, Good, Warn, Bad }

public sealed class WebsocketRow : ObservableObject
{
    public required string Name { get; init; }
    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
    private string _topics = "";
    public string Topics { get => _topics; set => Set(ref _topics, value); }
}

public sealed class ChannelRow : ObservableObject
{
    public required long Id { get; init; }
    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
    private StatusKind _statusKind;
    public StatusKind StatusKind { get => _statusKind; set => Set(ref _statusKind, value); }
    private string _game = "";
    public string Game { get => _game; set => Set(ref _game, value); }
    private string _drops = "";
    public string Drops { get => _drops; set => Set(ref _drops, value); }
    private int? _viewers;
    public int? Viewers { get => _viewers; set => Set(ref _viewers, value); }
    private string _acl = "";
    public string Acl { get => _acl; set => Set(ref _acl, value); }
    private bool _isWatching;
    public bool IsWatching { get => _isWatching; set => Set(ref _isWatching, value); }
}

public sealed class BenefitView : ObservableObject
{
    public required string Name { get; init; }
    private ImageSource? _image;
    public ImageSource? Image { get => _image; set => Set(ref _image, value); }
}

public sealed class DropView : ObservableObject
{
    public required TimedDrop Drop { get; init; }
    public required List<BenefitView> Benefits { get; init; }
    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }
    private StatusKind _progressKind;
    public StatusKind ProgressKind { get => _progressKind; set => Set(ref _progressKind, value); }
    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public void Refresh()
    {
        var drop = Drop;
        Progress = drop.Progress;
        string text;
        var kind = StatusKind.Normal;
        if (drop.IsClaimed)
        {
            kind = StatusKind.Good;
            text = L.T("gui.inventory.status.claimed", "Claimed ✔");
        }
        else if (drop.CanClaim)
        {
            kind = StatusKind.Warn;
            text = L.T("gui.inventory.status.ready_to_claim", "Ready to claim ⏳");
        }
        else if (drop.CurrentMinutes > 0 || drop.CanEarn())
        {
            text = L.F("gui.inventory.percent_progress", "{percent} of {minutes} minutes",
                ("percent", drop.Progress.ToString("P1")), ("minutes", drop.RequiredMinutes));
            if (drop.EndsAt < drop.Campaign.EndsAt)
                text += "\n" + L.F("gui.inventory.ends", "Ends: {time}", ("time", Util.LocalTime(drop.EndsAt)));
        }
        else
        {
            text = drop.RequiredMinutes > 0
                ? L.F("gui.inventory.minutes_progress", "{minutes} minutes", ("minutes", drop.RequiredMinutes))
                : "";  // 0 минут — дропы за подписку
            if (DateTimeOffset.UtcNow < drop.StartsAt && drop.StartsAt > drop.Campaign.StartsAt)
                text += "\n" + L.F("gui.inventory.starts", "Starts: {time}", ("time", Util.LocalTime(drop.StartsAt)));
            else if (drop.EndsAt < drop.Campaign.EndsAt)
                text += "\n" + L.F("gui.inventory.ends", "Ends: {time}", ("time", Util.LocalTime(drop.EndsAt)));
        }
        ProgressText = text.Trim('\n');
        ProgressKind = kind;
    }
}

public sealed class CampaignView : ObservableObject
{
    public required DropsCampaign Campaign { get; init; }
    public required List<DropView> Drops { get; init; }
    public string Name => Campaign.Name;
    public string GameName => Campaign.Game.Name;
    public string LinkUrl => Campaign.LinkUrl;
    public bool Eligible => Campaign.Eligible;
    public string LinkText => Campaign.Eligible
        ? L.T("gui.inventory.status.linked", "Linked ✔")
        : L.T("gui.inventory.status.not_linked", "Not Linked ❌");
    public StatusKind LinkKind => Campaign.Eligible ? StatusKind.Good : StatusKind.Bad;

    public string TimeText => Campaign.Upcoming
        ? L.F("gui.inventory.starts", "Starts: {time}", ("time", Util.LocalTime(Campaign.StartsAt)))
        : L.F("gui.inventory.ends", "Ends: {time}", ("time", Util.LocalTime(Campaign.EndsAt)));
    public string TimeTooltip => Campaign.Upcoming
        ? L.F("gui.inventory.ends", "Ends: {time}", ("time", Util.LocalTime(Campaign.EndsAt)))
        : L.F("gui.inventory.starts", "Starts: {time}", ("time", Util.LocalTime(Campaign.StartsAt)));

    public string AllowedText
    {
        get
        {
            var acl = Campaign.AllowedChannels;
            string list;
            if (acl.Count == 0) list = L.T("gui.inventory.all_channels", "All");
            else if (acl.Count <= 5) list = string.Join("\n", acl.Select(c => c.Name));
            else list = string.Join("\n", acl.Take(4).Select(c => c.Name)) + "\n" +
                        L.F("gui.inventory.and_more", "and {amount} more...", ("amount", acl.Count - 4));
            return $"{L.T("gui.inventory.allowed_channels", "Allowed Channels:")}\n{list}";
        }
    }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    private StatusKind _statusKind;
    public StatusKind StatusKind { get => _statusKind; set => Set(ref _statusKind, value); }
    private ImageSource? _image;
    public ImageSource? Image { get => _image; set => Set(ref _image, value); }
    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    public void RefreshStatus()
    {
        if (Campaign.Active) { StatusText = L.T("gui.inventory.status.active", "Active ✔"); StatusKind = StatusKind.Good; }
        else if (Campaign.Upcoming) { StatusText = L.T("gui.inventory.status.upcoming", "Upcoming ⏳"); StatusKind = StatusKind.Warn; }
        else { StatusText = L.T("gui.inventory.status.expired", "Expired ❌"); StatusKind = StatusKind.Bad; }
    }
}

public sealed class InventoryFilter : ObservableObject
{
    private bool _notLinked, _upcoming = true, _expired, _excluded, _finished;
    public bool NotLinked { get => _notLinked; set { if (Set(ref _notLinked, value)) Changed?.Invoke(); } }
    public bool Upcoming { get => _upcoming; set { if (Set(ref _upcoming, value)) Changed?.Invoke(); } }
    public bool Expired { get => _expired; set { if (Set(ref _expired, value)) Changed?.Invoke(); } }
    public bool Excluded { get => _excluded; set { if (Set(ref _excluded, value)) Changed?.Invoke(); } }
    public bool Finished { get => _finished; set { if (Set(ref _finished, value)) Changed?.Invoke(); } }
    public event Action? Changed;

    /// <summary>Кампании для главной вкладки: активные, доступные и не исключённые.</summary>
    public static bool MatchesMain(DropsCampaign c, Settings s) =>
        c.RequiredMinutes > 0
        && c.Eligible
        && c.Active
        && !c.Finished
        && !s.Exclude.Contains(c.Game.Name)
        && (s.PriorityMode != PriorityMode.PriorityOnly || s.Priority.Contains(c.Game.Name));

    public bool Matches(DropsCampaign c, Settings s)
    {
        bool priorityOnly = s.PriorityMode == PriorityMode.PriorityOnly;
        return c.RequiredMinutes > 0  // кампании только за подписку не показываем
               && (NotLinked || c.Eligible)
               && (c.Active || (Upcoming && c.Upcoming) || (Expired && c.Expired))
               && (Excluded || (!s.Exclude.Contains(c.Game.Name) && !priorityOnly) || s.Priority.Contains(c.Game.Name))
               && (Finished || !c.Finished);
    }
}

/// <summary>Асинхронная загрузка картинок: декодирование в пуле потоков, уменьшенный размер.</summary>
public static class ImageLoader
{
    public static async Task<ImageSource?> LoadAsync(ImageCache cache, string? url, int decodeWidth)
    {
        var path = await cache.GetFileAsync(url);
        if (path is null) return null;
        return await Task.Run(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return (ImageSource?)bmp;
            }
            catch
            {
                try { File.Delete(path); } catch { /* файл занят */ }
                return null;
            }
        });
    }
}

public sealed class ObservableList<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
    }
}
