using Microsoft.Win32;

namespace TwitchDropsMiner.Core;

/// <summary>Автозапуск через HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TwitchDropsMiner";

    private static string SelfPath => $"\"{AppPaths.ExePath}\"";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && value.Contains(SelfPath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled, bool intoTray, int verbose)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var flags = new List<string>();
            if (verbose > 0) flags.Add("-" + new string('v', Math.Min(verbose, 4)));
            if (intoTray) flags.Add("--tray");
            key.SetValue(ValueName, $"{SelfPath} {string.Join(' ', flags)}".TrimEnd(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
