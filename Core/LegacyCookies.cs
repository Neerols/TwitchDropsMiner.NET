using System.Text;
using System.Text.RegularExpressions;

namespace TwitchDropsMiner.Core;

/// <summary>
/// Чтение cookies.jar Python-версии (pickle-файл aiohttp CookieJar).
/// Полноценный разбор pickle не нужен: строки в нём хранятся как SHORT_BINUNICODE
/// (0x8C, длина, UTF-8), поэтому достаточно найти строку "auth-token" и следующее за ней значение.
/// </summary>
public static partial class LegacyCookies
{
    [GeneratedRegex("^[a-z0-9]{30}$")] private static partial Regex TokenPattern();
    [GeneratedRegex("^[0-9a-f]{32}$")] private static partial Regex DeviceIdPattern();

    public static (string? token, string? deviceId) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var strings = new List<string>();
        for (int i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] != 0x8C) continue;  // SHORT_BINUNICODE
            int len = bytes[i + 1];
            if (len == 0 || i + 2 + len > bytes.Length) continue;
            try
            {
                strings.Add(new UTF8Encoding(false, true).GetString(bytes, i + 2, len));
                i += 1 + len;
            }
            catch (DecoderFallbackException) { }
        }
        return (ValueAfter(strings, "auth-token", TokenPattern()), ValueAfter(strings, "unique_id", DeviceIdPattern()));
    }

    private static string? ValueAfter(List<string> strings, string key, Regex valuePattern)
    {
        for (int i = 0; i < strings.Count; i++)
        {
            if (strings[i] != key) continue;
            for (int j = i + 1; j < Math.Min(strings.Count, i + 20); j++)
                if (valuePattern.IsMatch(strings[j])) return strings[j];
        }
        return null;
    }
}
