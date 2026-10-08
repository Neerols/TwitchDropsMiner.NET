using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using TwitchDropsMiner.Core;
using TwitchDropsMiner.UI;

namespace TwitchDropsMiner;

public partial class App : Application
{
    private const string Usage =
        "TwitchDropsMiner.exe [-v|-vv|-vvv|-vvvv] [--tray] [--log] [--dump] [--version]\n\n" +
        "  -v ... -vvvv  подробность журнала (Warning / Info / Call / Debug)\n" +
        "  --tray        запуск свёрнутым в трей\n" +
        "  --log         писать журнал в log.txt\n" +
        "  --dump        выгрузить данные кампаний в dump.dat и выйти\n" +
        "  --debug-ws    отладка websocket\n" +
        "  --debug-gql   отладка GQL\n" +
        "  --version     версия\n\n" +
        "Коды выхода: 0 — успех, 1 — критическая ошибка, 2 — неверные аргументы,\n" +
        "3 — приложение уже запущено, 4 — ошибка загрузки настроек.";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private int _exitCode;

    public static void ApplyTheme(AppTheme theme)
    {
        Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Debug($"Unobserved: {args.Exception.Message}"); args.SetObserved(); };

        // --- аргументы ---
        bool tray = false, log = false, dump = false;
        int verbose = 0;

        for (int ai = 0; ai < e.Args.Length; ai++)
        {
            var arg = e.Args[ai];
            switch (arg)
            {
                case "--probe":
                    // диагностика: какие GQL-данные отдаёт Twitch своему сайту на странице инвентаря
                    _ = Dispatcher.BeginInvoke(async () =>
                    {
                        var path = Path.Combine(AppPaths.DataDir, "probe.txt");
                        var items = await TwitchBrowser.CaptureGqlAsync("https://www.twitch.tv/drops/inventory",
                            [], TimeSpan.FromSeconds(25), CancellationToken.None);
                        File.WriteAllText(path, string.Join("\n\n=====\n", items.Select(i => "REQ: " + i.request + "\nRESP: " + i.response)));
                        _vm?.Print($"Probe: {items.Count} GQL responses -> {path}");
                    }, DispatcherPriority.ApplicationIdle);
                    break;

                case "--tray": tray = true; break;
                case "--log": log = true; break;
                case "--dump": dump = true; break;
                case "--debug-ws": Log.DebugWs = true; break;
                case "--debug-gql": Log.DebugGql = true; break;
                case "--version":
                    MessageBox.Show($"v{typeof(App).Assembly.GetName().Version?.ToString(3)}", "Twitch Drops Miner");
                    Shutdown(0);
                    return;
                case "-h" or "--help" or "/?":
                    MessageBox.Show(Usage, "Twitch Drops Miner");
                    Shutdown(0);
                    return;
                default:
                    if (arg.Length > 1 && arg[0] == '-' && arg[1..].All(c => c == 'v')) { verbose += arg.Length - 1; break; }
                    MessageBox.Show($"Неизвестный аргумент: {arg}\n\n{Usage}", "Argument Parser Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(2);
                    return;
            }
        }

        // --- один экземпляр на папку данных ---
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDir.ToLowerInvariant())))[..16];
        _singleInstance = new Mutex(true, $"Local\\TwitchDropsMiner.NET.{id}", out bool createdNew);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\TwitchDropsMiner.NET.{id}.show");
        if (!createdNew)
        {
            _showSignal.Set();  // просим запущенный экземпляр показать окно
            Shutdown(3);
            return;
        }

        // --- настройки ---
        Settings settings;
        try
        {
            settings = Settings.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"There was an error while loading the settings file:\n\n{ex}", "Settings error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(4);
            return;
        }
        settings.ArgTray = tray;
        settings.ArgLog = log;
        settings.ArgDump = dump;
        settings.ArgVerbose = verbose;

        L.SetLanguage(settings.Language);
        Log.Level = Math.Min(verbose, 4) switch
        {
            0 => LogLevel.Error,
            1 => LogLevel.Warning,
            2 => LogLevel.Info,
            3 => LogLevel.Call,
            _ => LogLevel.Debug,
        };
        if (log) Log.EnableFile(AppPaths.LogFile);
        ApplyTheme(settings.Theme);

        // --- окно, трей, ядро ---
        _tray = new TrayIcon();
        _vm = new MainViewModel(settings, _tray);
        Log.Sink = _vm.Print;
        _window = new MainWindow(_vm);
        _window.ExitRequested += RequestExit;
        _tray.ShowRequested += () => _window.ShowFromTray();
        _tray.QuitRequested += RequestExit;
        if (Log.Level < LogLevel.Error) _vm.Print($"Logging level: {Log.Level}");
        if (!settings.StartInTray) _window.Show();
        ListenForShowSignal();
        SessionEnding += (_, _) => { try { settings.Save(); } catch { } RequestExit(); };

        _ = RunMinerAsync();
    }

    private void ListenForShowSignal()
    {
        var signal = _showSignal!;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try { signal.WaitOne(); } catch { return; }
                Dispatcher.BeginInvoke(() => _window?.ShowFromTray());
            }
        }) { IsBackground = true, Name = "SingleInstanceSignal" };
        thread.Start();
    }

    private void RequestExit() => _vm?.Miner.Close();

    private async Task RunMinerAsync()
    {
        try
        {

            await _vm!.Miner.RunAsync();
        }
        catch (Exception ex)
        {
            _exitCode = 1;
            Log.Error(ex.ToString());
            MessageBox.Show(ex.ToString(), L.T("x.fatal", "Fatal error:"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Log.Close();
            if (_window is not null)
            {
                _window.AllowClose = true;
                _window.Close();
            }
            _tray?.Dispose();
            _singleInstance?.ReleaseMutex();
            Shutdown(_exitCode);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Unhandled UI exception: {e.Exception}");
        _vm?.Print($"{L.T("x.fatal", "Fatal error:")} {e.Exception.Message}");
        e.Handled = true;
    }
}
