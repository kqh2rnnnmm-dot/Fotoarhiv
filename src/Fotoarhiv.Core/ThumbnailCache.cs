using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Fotoarhiv.Core;

public sealed record ThumbnailPixels(byte[] EncodedImage, int Width, int Height, int OriginalWidth, int OriginalHeight);
public sealed record ThumbnailResult(ThumbnailPixels? Image, bool IsOffline, bool Persisted, bool SourceChanged = false, string? Reason = null);

/// <summary>Bounded background requests and atomic, checksummed application-owned cache entries.
/// The decoder may only read the source. This class writes only below its cache root.</summary>
public sealed class ThumbnailCache : IAsyncDisposable
{
    private const int HeaderSize = 60, MaxBytes = 2 * 1024 * 1024;
    private static readonly byte[] Magic = "FATHMB01"u8.ToArray();
    private readonly string _root;
    private readonly Func<string, CancellationToken, ValueTask<ThumbnailPixels>> _decode;
    private readonly Func<ThumbnailPixels, bool>? _validate;
    private readonly Channel<Request> _queue;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task[] _workers;
    private int _disposed, _active, _maximum;
    public int MaximumObservedActive => Volatile.Read(ref _maximum);
    private sealed record Request(PhotoRecord Photo, CancellationToken Token, TaskCompletionSource<ThumbnailResult> Completion);

    public ThumbnailCache(string root, Func<string, CancellationToken, ValueTask<ThumbnailPixels>> decode,
        int workers = 4, int capacity = 128, Func<ThumbnailPixels, bool>? validate = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1); ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _root = Path.GetFullPath(root); _decode = decode; _validate = validate;
        _queue = Channel.CreateBounded<Request>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = workers == 1 });
        _workers = Enumerable.Range(0, workers).Select(_ => Task.Run(WorkerAsync)).ToArray();
    }
    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string KeyFor(PhotoRecord photo) => Digest($"thumbnail-v1\n{PathIdentity.Normalize(photo.Path)}\n{photo.Size}\n{photo.ModifiedUtcTicks}\n{photo.Sha256?.ToUpperInvariant()}");
    private string Folder(PhotoRecord photo) => Path.Combine(_root, Digest(PathIdentity.Normalize(photo.Path)));
    private static bool IsKey(string key) => key.Length == 64 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<ThumbnailResult> GetAsync(PhotoRecord photo, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // Snapshot mutable catalog objects before a later scan can replace their version/hash.
        var snapshot = new PhotoRecord { Path = photo.Path, Size = photo.Size, ModifiedUtcTicks = photo.ModifiedUtcTicks, Sha256 = photo.Sha256 };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var completion = new TaskCompletionSource<ThumbnailResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
        if (!_queue.Writer.TryWrite(new Request(snapshot, linked.Token, completion)))
            return new(null, false, false, Reason: "Очередь миниатюр занята");
        return await completion.Task.ConfigureAwait(false);
    }
    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (request.Token.IsCancellationRequested) { request.Completion.TrySetCanceled(request.Token); continue; }
                int active = Interlocked.Increment(ref _active);
                int observed; do { observed = Volatile.Read(ref _maximum); if (active <= observed) break; } while (Interlocked.CompareExchange(ref _maximum, active, observed) != observed);
                try { request.Completion.TrySetResult(await ReadOrCreateAsync(request.Photo, request.Token).ConfigureAwait(false)); }
                catch (OperationCanceledException) { request.Completion.TrySetCanceled(request.Token); }
                catch (Exception) { request.Completion.TrySetResult(new(null, false, false, Reason: "Миниатюра недоступна")); }
                finally { Interlocked.Decrement(ref _active); }
            }
        }
        catch (OperationCanceledException) { }
        finally { while (_queue.Reader.TryRead(out var pending)) pending.Completion.TrySetCanceled(_lifetime.Token); }
    }
    private static PhotoRecord? VersionOf(string path)
    {
        try
        {
            var info = new FileInfo(path); info.Refresh();
            return info.Exists ? new PhotoRecord { Path = path, Size = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
    private static bool SameVersion(PhotoRecord a, PhotoRecord? b) => b is not null && a.Size == b.Size && a.ModifiedUtcTicks == b.ModifiedUtcTicks;
    private async Task<ThumbnailResult> ReadOrCreateAsync(PhotoRecord photo, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string folder = Folder(photo); var actual = VersionOf(photo.Path);
        if (actual is null)
        {
            var latest = await ReadLatestAsync(folder, token).ConfigureAwait(false);
            var cached = latest ?? await ReadEntryAsync(Path.Combine(folder, KeyFor(photo) + ".thumb"), token).ConfigureAwait(false);
            return new(cached, true, cached is not null, Reason: cached is null ? "Источник и миниатюра недоступны" : "Источник недоступен; миниатюра из кеша");
        }
        bool changed = !SameVersion(photo, actual); if (!changed) actual.Sha256 = photo.Sha256;
        string key = KeyFor(actual), entry = Path.Combine(folder, key + ".thumb");
        var ready = await ReadEntryAsync(entry, token).ConfigureAwait(false);
        if (ready is not null) return SameVersion(actual, VersionOf(photo.Path)) ? new(ready, false, true, changed) : new(null, false, false, true, "Файл изменился во время чтения");
        ThumbnailPixels pixels;
        try { pixels = await _decode(photo.Path, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or OutOfMemoryException)
        { return new(null, false, false, changed, "Файл не прочитан или формат не поддерживается"); }
        token.ThrowIfCancellationRequested();
        if (!SameVersion(actual, VersionOf(photo.Path))) return new(null, false, false, true, "Файл изменился во время чтения");
        if (!ValidSize(pixels) || (_validate is not null && !_validate(pixels))) return new(null, false, false, changed, "Миниатюра не прочитана");
        try
        {
            Directory.CreateDirectory(folder);
            await AtomicWriteAsync(entry, Pack(pixels), token).ConfigureAwait(false);
            await AtomicWriteAsync(Path.Combine(folder, "latest"), Encoding.ASCII.GetBytes(key), token).ConfigureAwait(false);
            return new(pixels, false, true, changed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        { return new(pixels, false, false, changed, "Миниатюра показана, но не сохранена в кеш"); }
    }
    private async Task<ThumbnailPixels?> ReadLatestAsync(string folder, CancellationToken token)
    {
        try
        {
            string pointer = Path.Combine(folder, "latest");
            if (!File.Exists(pointer) || new FileInfo(pointer).Length != 64) return null;
            string key = await File.ReadAllTextAsync(pointer, token).ConfigureAwait(false);
            return IsKey(key) ? await ReadEntryAsync(Path.Combine(folder, key + ".thumb"), token).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private async Task<ThumbnailPixels?> ReadEntryAsync(string path, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(path); if (!info.Exists || info.Length < HeaderSize || info.Length > MaxBytes + HeaderSize) return null;
            var data = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (data.Length < HeaderSize || !data.AsSpan(0, 8).SequenceEqual(Magic)) return null;
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24, 4));
            if (length < 1 || length > MaxBytes || length != data.Length - HeaderSize) return null;
            var payload = data.AsSpan(HeaderSize).ToArray();
            if (!CryptographicOperations.FixedTimeEquals(EnvelopeHash(data.AsSpan(0, 28), payload), data.AsSpan(28, 32))) return null;
            var result = new ThumbnailPixels(payload, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20)));
            return ValidSize(result) && (_validate is null || _validate(result)) ? result : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
    private static bool ValidSize(ThumbnailPixels p) => p.EncodedImage.Length is > 0 and <= MaxBytes && p.Width is > 0 and <= 1024 && p.Height is > 0 and <= 1024 && p.OriginalWidth is > 0 and <= 1_000_000 && p.OriginalHeight is > 0 and <= 1_000_000;
    private static byte[] Pack(ThumbnailPixels pixels)
    {
        var data = new byte[HeaderSize + pixels.EncodedImage.Length]; Magic.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), pixels.Width); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), pixels.Height);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), pixels.OriginalWidth); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20), pixels.OriginalHeight);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), pixels.EncodedImage.Length); EnvelopeHash(data.AsSpan(0, 28), pixels.EncodedImage).CopyTo(data, 28);
        pixels.EncodedImage.CopyTo(data, HeaderSize); return data;
    }
    private static byte[] EnvelopeHash(ReadOnlySpan<byte> header, ReadOnlySpan<byte> pixels)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header); hash.AppendData(pixels); return hash.GetHashAndReset();
    }
    private static async Task AtomicWriteAsync(string destination, byte[] data, CancellationToken token)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, data, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite: true); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete(); _lifetime.Cancel(); await Task.WhenAll(_workers).ConfigureAwait(false); _lifetime.Dispose();
    }
}
