using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TwitchDropsMiner.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>true — окно закрывается по-настоящему (ядро уже остановлено).</summary>
    public bool AllowClose { get; set; }

    /// <summary>Пользователь запросил выход (крестик без «сворачивать в трей» или «Закрыть» в трее).</summary>
    public event Action? ExitRequested;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Title = $"Twitch Drops Miner .NET v{typeof(App).Assembly.GetName().Version?.ToString(3)}";
        vm.ShowWindowAction = ShowFromTray;
        vm.MinimizeToTrayAction = Hide;
        vm.LogAppended += ScrollLogToEnd;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ScrollLogToEnd()
    {
        // не мешаем пользователю, который выделяет строки журнала
        if (LogList.Items.Count > 0 && LogList.SelectedItems.Count == 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        // Esc снимает выделение (выбранный канал больше не форсируется при переключении)
        _vm.SelectedChannel = null;
        _vm.SelectedPriority = null;
        _vm.SelectedExclude = null;
        LogList.UnselectAll();
        Keyboard.ClearFocus();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;  // событие всплывает и от вложенных списков
        if (Tabs.SelectedIndex == 1) _vm.RefreshInventory();
    }

    private void ProxyBox_LostFocus(object sender, RoutedEventArgs e) => _vm.CommitProxy();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowClose)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (_vm.Settings.CloseToTray) Hide();
        else ExitRequested?.Invoke();
    }
}
