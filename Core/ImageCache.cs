using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TwitchDropsMiner.Core;

/// <summary>
/// Дисковый кеш картинок кампаний и наград. Файлы, не использовавшиеся 7 дней, удаляются при запуске.
/// </summary>
public sealed class ImageCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private readonly Func<HttpService?> _http;
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();
    private readonly SemaphoreSlim _downloads = new(6, 6);

    public ImageCache(Func<HttpService?> http)
    {
        _http = http;
        Directory.CreateDirectory(AppPaths.CacheDir);
        _ = Task.Run(Cleanup);
    }

    private static void Cleanup()
    {
        try
        {
            var limit = DateTime.UtcNow - Lifetime;
            foreach (var file in new DirectoryInfo(AppPaths.CacheDir).EnumerateFiles())
                if (file.LastWriteTimeUtc < limit || file.Name.EndsWith(".tmp"))
                    file.Delete();
        }
        catch (Exception ex)
        {
            Log.Debug($"Image cache cleanup: {ex.Message}");
        }
    }

    private static string FileFor(string url)
    {
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
        var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")) ext = ".img";
        return Path.Combine(AppPaths.CacheDir, hash + ext);
    }

    /// <summary>Локальный путь к картинке (скачивает при необходимости) или null.</summary>
    public Task<string?> GetFileAsync(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
            return Task.FromResult<string?>(null);
        return _inflight.GetOrAdd(url, u => Task.Run(() => LoadAsync(u)));
    }

    private async Task<string?> LoadAsync(string url)
    {
        var path = FileFor(url);
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                if (DateTime.UtcNow - fi.LastWriteTimeUtc > TimeSpan.FromDays(1))
                    fi.LastWriteTimeUtc = DateTime.UtcNow;  // продлеваем срок жизни
                return path;
            }
            var http = _http();
            if (http is null) return null;
            await _downloads.WaitAsync().ConfigureAwait(false);
            try
            {
                var r = await http.RequestAsync(HttpMethod.Get, url).ConfigureAwait(false);
                if (r.Status != 200 || r.Body.Length == 0) return null;
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, r.Body).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
                return path;
            }
            finally { _downloads.Release(); }
        }
        catch (Exception ex)
        {
            Log.Debug($"Image load failed {url}: {ex.Message}");
            _inflight.TryRemove(url, out _);
            return null;
        }
    }
}
