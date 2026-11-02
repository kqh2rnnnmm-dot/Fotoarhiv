using Fotoarhiv.Core;
using Xunit;

namespace Fotoarhiv.Tests;

public sealed class ThumbnailCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Fotoarhiv-P1-cache-" + Guid.NewGuid());
    private string CacheRoot => Path.Combine(_root, "application", "Cache", "Thumbnails");
    private PhotoRecord CreatePhoto(string name = "original.bin")
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, name); File.WriteAllBytes(path, [1, 2, 3]);
        return new PhotoRecord { Path = path, Size = 3, ModifiedUtcTicks = File.GetLastWriteTimeUtc(path).Ticks };
    }
    private static ThumbnailPixels Pixels(byte value = 7) => new([value, 9, 8], 32, 24, 1200, 900);

    [Fact]
    public void Cache_Keys_Identify_Path_Version_And_Known_Content_Hash()
    {
        var photo = new PhotoRecord { Path = @"C:\Photos\a.jpg", Size = 42, ModifiedUtcTicks = 123, Sha256 = "ABCD" };
        var key = ThumbnailCache.KeyFor(photo); photo.Path = @"c:/PHOTOS/a.jpg"; Assert.Equal(key, ThumbnailCache.KeyFor(photo));
        photo.Path = @"C:\Other\a.jpg"; Assert.NotEqual(key, ThumbnailCache.KeyFor(photo));
        photo.Path = @"C:\Photos\a.jpg"; photo.Size++; Assert.NotEqual(key, ThumbnailCache.KeyFor(photo));
        photo.Size--; photo.ModifiedUtcTicks++; Assert.NotEqual(key, ThumbnailCache.KeyFor(photo));
        photo.ModifiedUtcTicks--; photo.Sha256 = "other"; Assert.NotEqual(key, ThumbnailCache.KeyFor(photo));
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public async Task A_New_Service_Uses_Persisted_Thumbnail_Without_Decoding_Original()
    {
        var photo = CreatePhoto(); var original = File.ReadAllBytes(photo.Path); var stamp = File.GetLastWriteTimeUtc(photo.Path);
        await using (var cache = new ThumbnailCache(CacheRoot, (_, _) => ValueTask.FromResult(Pixels())))
        { var result = await cache.GetAsync(photo); Assert.True(result.Persisted); Assert.False(result.IsOffline); }
        int decoded = 0;
        await using (var cache = new ThumbnailCache(CacheRoot, (_, _) => { decoded++; return ValueTask.FromResult(Pixels(99)); }))
        { Assert.Equal(Pixels().EncodedImage, (await cache.GetAsync(photo)).Image!.EncodedImage); }
        Assert.Equal(0, decoded); Assert.Equal(original, File.ReadAllBytes(photo.Path)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(photo.Path));
        Assert.All(Directory.GetFiles(CacheRoot, "*", SearchOption.AllDirectories), path => Assert.DoesNotContain("original", Path.GetFileName(path)));
        Assert.Equal(new[] { "application", "original.bin" }, Directory.GetFileSystemEntries(_root).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public async Task Offline_Returns_Last_Persisted_Thumbnail_With_Explicit_Offline_State()
    {
        var photo = CreatePhoto();
        await using (var cache = new ThumbnailCache(CacheRoot, (_, _) => ValueTask.FromResult(Pixels()))) await cache.GetAsync(photo);
        File.Move(photo.Path, photo.Path + ".own-fixture-offline");
        await using var offline = new ThumbnailCache(CacheRoot, (_, _) => throw new Exception("Must not decode absent original"));
        var result = await offline.GetAsync(photo); Assert.True(result.IsOffline); Assert.NotNull(result.Image); Assert.Equal(1200, result.Image.OriginalWidth);
    }

    [Fact]
    public async Task A_Changed_File_Is_Decoded_Again_Even_Before_Catalog_Rescan()
    {
        var photo = CreatePhoto(); int decoded = 0;
        await using var cache = new ThumbnailCache(CacheRoot, (_, _) => ValueTask.FromResult(Pixels((byte)++decoded)));
        var before = await cache.GetAsync(photo); File.WriteAllBytes(photo.Path, [4, 5, 6, 7]);
        var after = await cache.GetAsync(photo);
        Assert.Equal(2, decoded); Assert.True(after.SourceChanged); Assert.NotEqual(before.Image!.EncodedImage, after.Image!.EncodedImage);
    }

    [Fact]
    public async Task Corrupt_Cache_Regenerates_When_Online_And_Is_Not_Shown_When_Offline()
    {
        var photo = CreatePhoto(); int calls = 0;
        await using var cache = new ThumbnailCache(CacheRoot, (_, _) => ValueTask.FromResult(Pixels((byte)++calls)));
        await cache.GetAsync(photo);
        var path = Directory.GetFiles(CacheRoot, "*.thumb", SearchOption.AllDirectories).Single(); await File.WriteAllBytesAsync(path, [0, 1]);
        Assert.NotNull((await cache.GetAsync(photo)).Image); Assert.Equal(2, calls);
        await File.WriteAllBytesAsync(path, [0, 1]); File.Move(photo.Path, photo.Path + ".own-fixture-offline");
        var result = await cache.GetAsync(photo); Assert.True(result.IsOffline); Assert.Null(result.Image);
    }

    [Fact]
    public async Task A_Source_Changed_During_Decode_Is_Neither_Persisted_Nor_Returned_As_Current()
    {
        var photo = CreatePhoto();
        await using var cache = new ThumbnailCache(CacheRoot, (path, _) => { File.WriteAllBytes(path, [8, 9, 10, 11]); return ValueTask.FromResult(Pixels()); });
        var result = await cache.GetAsync(photo); Assert.Null(result.Image); Assert.True(result.SourceChanged);
        Assert.Empty(Directory.Exists(CacheRoot) ? Directory.GetFiles(CacheRoot, "*.thumb", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Cache_Write_Failure_Does_Not_Modify_The_Source_Or_Crash_The_Loader()
    {
        var photo = CreatePhoto(); var rootFile = Path.Combine(_root, "not-a-directory"); File.WriteAllBytes(rootFile, [3]);
        await using var cache = new ThumbnailCache(rootFile, (_, _) => ValueTask.FromResult(Pixels()));
        var result = await cache.GetAsync(photo); Assert.NotNull(result.Image); Assert.False(result.Persisted); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(photo.Path));
    }

    [Fact]
    public async Task Requests_Are_Bounded_And_Cancelled_Queue_Items_Do_Not_Decode()
    {
        var photo = CreatePhoto(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int decoded = 0;
        await using var cache = new ThumbnailCache(CacheRoot, async (_, ct) => { Interlocked.Increment(ref decoded); started.TrySetResult(); await release.Task.WaitAsync(ct); return Pixels(); }, workers: 1, capacity: 2);
        var first = cache.GetAsync(photo); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource(); var queued = cache.GetAsync(photo, cancelled.Token); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.TrySetResult(); await first; Assert.Equal(1, decoded);
        Assert.InRange(cache.MaximumObservedActive, 1, 1);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
