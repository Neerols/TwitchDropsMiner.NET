namespace TwitchDropsMiner.Core;

/// <summary>
/// Пути к файлам приложения.
/// По умолчанию данные хранятся в %LOCALAPPDATA%\TwitchDropsMiner — рядом с exe ничего не создаётся.
/// Портативный режим: пустой файл portable.txt рядом с exe — тогда данные хранятся рядом с exe.
/// </summary>
public static class AppPaths
{
    public const string PortableMarker = "portable.txt";

    public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TwitchDropsMiner.exe");
    public static string ExeDir { get; } = Path.GetDirectoryName(ExePath)!;
    public static bool IsPortable { get; } = File.Exists(Path.Combine(ExeDir, PortableMarker));
    public static string DataDir { get; } = IsPortable
        ? ExeDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwitchDropsMiner");

    public static string ConfigDir => Path.Combine(DataDir, "config");
    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string AuthFile => Path.Combine(ConfigDir, "auth.bin");
    public static string LogFile => Path.Combine(DataDir, "log.txt");
    public static string DumpFile => Path.Combine(DataDir, "dump.dat");
    /// <summary>settings.json Python-версии (рядом с exe) — только для импорта настроек.</summary>
    public static string LegacySettingsFile => Path.Combine(ExeDir, "config", "settings.json");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>
    /// Переносит данные прежних версий (хранившиеся рядом с exe) в папку данных.
    /// Вызывается один раз при запуске, после проверки «одного экземпляра».
    /// Возвращает описание результата для журнала или null, если переносить нечего.
    /// </summary>
    public static string? MigrateFromExeDir()
    {
        if (IsPortable || string.Equals(Path.GetFullPath(ExeDir), Path.GetFullPath(DataDir), StringComparison.OrdinalIgnoreCase))
            return null;
        // признаки данных .NET-версии: файл входа или профиль браузера
        var exeSettings = Path.Combine(ExeDir, "config", "settings.json");
        bool hasOurData = File.Exists(Path.Combine(ExeDir, "config", "auth.bin"))
                          || Directory.Exists(Path.Combine(ExeDir, "browser"))
                          || (File.Exists(exeSettings) && File.ReadAllText(exeSettings).Contains("\"theme\""));
        if (!hasOurData) return null;
        // в новой папке уже есть вход — не перезаписываем
        if (File.Exists(AuthFile) || Directory.Exists(Path.Combine(DataDir, "browser")))
            return $"Data next to the exe was not moved: {DataDir} already has data";
        Directory.CreateDirectory(DataDir);
        var moved = new List<string>();
        try
        {
            foreach (var name in new[] { "config", "browser", "cache" })
            {
                var src = Path.Combine(ExeDir, name);
                if (!Directory.Exists(src)) continue;
                CopyDirectory(src, Path.Combine(DataDir, name));
                Directory.Delete(src, recursive: true);
                moved.Add(name);
            }
            foreach (var name in new[] { "log.txt", "dump.dat" })
            {
                var src = Path.Combine(ExeDir, name);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(DataDir, name), overwrite: true);
                File.Delete(src);
                moved.Add(name);
            }
            return moved.Count > 0 ? $"Moved {string.Join(", ", moved)} from {ExeDir} to {DataDir}" : null;
        }
        catch (Exception ex)
        {
            return $"Data migration from {ExeDir} failed ({ex.Message}); copied: {string.Join(", ", moved)}";
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }
}
