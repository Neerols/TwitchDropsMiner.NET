using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TwitchDropsMiner.Core;

#region Исключения

public class MinerException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class GqlException(string message) : MinerException(message);
public sealed class RequestInvalidException() : MinerException("Request invalidated");
public sealed class ReloadRequestException() : Exception("Reload requested");

#endregion

/// <summary>Безопасный доступ к полям JsonNode.</summary>
public static class J
{
    public static JsonNode? At(this JsonNode? node, params object[] path)
    {
        foreach (var key in path)
        {
            if (node is null) return null;
            node = key switch
            {
                string s when node is JsonObject o => o.TryGetPropertyValue(s, out var v) ? v : null,
                int i when node is JsonArray a => i < a.Count ? a[i] : null,
                _ => null,
            };
        }
        return node;
    }

    public static string? Str(this JsonNode? node, params object[] path)
    {
        var n = node.At(path);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            return v.ToJsonString().Trim('"');
        }
        return null;
    }

    public static long Long(this JsonNode? node, params object[] path)
    {
        var n = node.At(path);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out l)) return l;
        }
        return 0;
    }

    public static int Int(this JsonNode? node, params object[] path) => (int)node.Long(path);

    public static bool Bool(this JsonNode? node, params object[] path)
    {
        var n = node.At(path);
        return n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    public static bool Has(this JsonNode? node, string key) =>
        node is JsonObject o && o.ContainsKey(key);

    public static bool IsNull(this JsonNode? node, params object[] path) => node.At(path) is null;

    public static IEnumerable<JsonNode> Arr(this JsonNode? node, params object[] path) =>
        node.At(path) is JsonArray a ? a.OfType<JsonNode>() : [];

    /// <summary>Разбор времени Twitch: "2024-01-01T00:00:00Z" или с долями секунды.</summary>
    public static DateTimeOffset Time(this JsonNode? node, params object[] path)
    {
        var s = node.Str(path);
        if (s is null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt : DateTimeOffset.MinValue;
    }

    public static string Minify(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    /// <summary>
    /// Рекурсивное слияние двух JSON-объектов: словари сливаются, у остальных значений побеждает primary.
    /// </summary>
    public static JsonObject Merge(JsonObject primary, JsonObject secondary)
    {
        var merged = new JsonObject();
        foreach (var key in primary.Select(p => p.Key).Union(secondary.Select(p => p.Key)))
        {
            primary.TryGetPropertyValue(key, out var vp);
            secondary.TryGetPropertyValue(key, out var vs);
            bool inP = primary.ContainsKey(key), inS = secondary.ContainsKey(key);
            if (inP && inS && vp is JsonObject op && vs is JsonObject os)
                merged[key] = Merge(op, os);
            else if (inP)
                merged[key] = vp?.DeepClone();
            else
                merged[key] = vs?.DeepClone();
        }
        return merged;
    }
}

public static class Util
{
    public const string CharsAscii = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    public const string CharsHexLower = "0123456789abcdef";

    public static string Nonce(string chars, int length)
    {
        Span<char> buf = stackalloc char[length];
        for (int i = 0; i < length; i++) buf[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(buf);
    }

    public static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size) =>
        source.Chunk(size).Select(c => c.ToList());

    public static string IsoNow() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string LocalTime(DateTimeOffset dt) =>
        dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось открыть ссылку {url}: {ex.Message}");
        }
    }
}

/// <summary>Экспоненциальная задержка с разбросом.</summary>
public sealed class ExponentialBackoff(double maximum = 300, double @base = 2, double variance = 0.1, double shift = 0)
{
    public int Steps { get; private set; }

    public double Next()
    {
        double value = Math.Pow(@base, Steps) * (1 - variance + Random.Shared.NextDouble() * 2 * variance) + shift;
        if (value > maximum) return maximum;
        Steps++;
        return value;
    }

    public void Reset() => Steps = 0;
}

/// <summary>
/// Ограничитель частоты: не более capacity одновременных запросов и не более capacity запусков за окно.
/// GQL Twitch очень чувствителен к лимитам — значения по умолчанию не менять.
/// </summary>
public sealed class RateLimiter(int capacity, TimeSpan window)
{
    private readonly SemaphoreSlim _concurrent = new(capacity, capacity);
    private readonly Queue<DateTime> _starts = new();
    private readonly SemaphoreSlim _windowLock = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _concurrent.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _windowLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var now = DateTime.UtcNow;
                    while (_starts.Count > 0 && now - _starts.Peek() >= window) _starts.Dequeue();
                    if (_starts.Count < capacity) { _starts.Enqueue(now); break; }
                    var wait = window - (now - _starts.Peek());
                    await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, ct).ConfigureAwait(false);
                }
            }
            finally { _windowLock.Release(); }
        }
        catch
        {
            _concurrent.Release();
            throw;
        }
        return new Releaser(_concurrent);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) sem.Release(); }
    }
}

/// <summary>Асинхронный аналог asyncio.Event.</summary>
public sealed class AsyncEvent
{
    private volatile TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsSet => _tcs.Task.IsCompleted;

    public void Set() => _tcs.TrySetResult();

    public void Clear()
    {
        while (true)
        {
            var tcs = _tcs;
            if (!tcs.Task.IsCompleted) return;
            if (Interlocked.CompareExchange(ref _tcs, new(TaskCreationOptions.RunContinuationsAsynchronously), tcs) == tcs) return;
        }
    }

    public Task WaitAsync(CancellationToken ct = default) => _tcs.Task.WaitAsync(ct);

    /// <summary>Ждёт событие не дольше timeout. true — если событие наступило.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) return IsSet;
        try
        {
            await _tcs.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

/// <summary>Значение, которого можно дождаться (аналог AwaitableValue).</summary>
public sealed class AwaitableValue<T> where T : class
{
    private T? _value;
    private readonly AsyncEvent _event = new();

    public bool HasValue => _event.IsSet;
    public T? Value => _event.IsSet ? _value : null;

    public async Task<T> GetAsync(CancellationToken ct)
    {
        while (true)
        {
            await _event.WaitAsync(ct);
            if (_value is not null && _event.IsSet) return _value;
        }
    }

    public void Set(T value) { _value = value; _event.Set(); }
    public void Clear() => _event.Clear();
}
