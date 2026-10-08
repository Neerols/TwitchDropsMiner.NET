namespace TwitchDropsMiner.Core;

/// <summary>
/// Пути к файлам приложения.
/// Если папка с exe доступна на запись — работаем «портативно» (данные рядом с exe, как в оригинале),
/// иначе (например, Program Files) — в %LOCALAPPDATA%\TwitchDropsMiner.
/// </summary>
public static class AppPaths
{
    public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TwitchDropsMiner.exe");
    public static string ExeDir { get; } = Path.GetDirectoryName(ExePath)!;
    public static string DataDir { get; } = ResolveDataDir();

    public static string ConfigDir => Path.Combine(DataDir, "config");
    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string AuthFile => Path.Combine(ConfigDir, "auth.bin");
    public static string LogFile => Path.Combine(DataDir, "log.txt");
    public static string DumpFile => Path.Combine(DataDir, "dump.dat");
    public static string LegacySettingsFile => Path.Combine(ExeDir, "config", "settings.json");

    private static string ResolveDataDir()
    {
        try
        {
            var probe = Path.Combine(ExeDir, $".write_test_{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return ExeDir;
        }
        catch
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwitchDropsMiner");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(CacheDir);
    }
}
