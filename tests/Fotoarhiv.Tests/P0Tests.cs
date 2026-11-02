using System.Text;
using Fotoarhiv.Core;
using Xunit;

namespace Fotoarhiv.Tests;

public sealed class P0Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Fotoarhiv-P0-" + Guid.NewGuid().ToString("N"));
    public P0Tests() => Directory.CreateDirectory(_root);
    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // A valid 1x1, 24-bit BMP without EXIF; synthetic and local.
    private static byte[] Bmp(byte pixel = 1)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(58); w.Write(0); w.Write(54);
        w.Write(40); w.Write(1); w.Write(1); w.Write((short)1); w.Write((short)24);
        w.Write(0); w.Write(4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        w.Write(new byte[] { pixel, 2, 3, 0 });
        return stream.ToArray();
    }

    [Fact]
    public async Task Cached_Hash_Is_Rejected_When_Source_Changed_Before_Duplicate_Search()
    {
        var a = Write("a.bmp", Bmp());
        Write("b.bmp", Bmp());
        var photos = await new PhotoScanner().ScanAsync([_root], []);
        Assert.Single(await new DuplicateFinder().FindAsync(photos));
        File.WriteAllBytes(a, Bmp(9));
        File.SetLastWriteTimeUtc(a, File.GetLastWriteTimeUtc(a).AddSeconds(10));
        Assert.Empty(await new DuplicateFinder().FindAsync(photos));
        Assert.Null(photos.Single(p => p.Path == a).Sha256);
    }

    [Fact]
    public async Task Rescan_Invalidates_Changed_File_And_Preserves_Unchanged_Hash()
    {
        var a = Write("a.bmp", Bmp());
        Write("sub/b.bmp", Bmp());
        var scanner = new PhotoScanner();
        var photos = await scanner.ScanAsync([_root], []);
        await new DuplicateFinder().FindAsync(photos);
        var hash = photos.Single(p => p.Name == "b.bmp").Sha256;
        File.WriteAllBytes(a, Bmp(9));
        File.SetLastWriteTimeUtc(a, File.GetLastWriteTimeUtc(a).AddSeconds(10));
        var rescanned = await scanner.ScanAsync([_root], photos);
        Assert.Null(rescanned.Single(p => p.Name == "a.bmp").Sha256);
        Assert.Equal(hash, rescanned.Single(p => p.Name == "b.bmp").Sha256);
        Assert.Empty(await new DuplicateFinder().FindAsync(rescanned));
    }

    [Fact]
    public async Task Real_Image_Without_Exif_Uses_Labelled_File_Date_And_No_Gps()
    {
        var path = Write("a.bmp", Bmp());
        var photo = Assert.Single(await new PhotoScanner().ScanAsync([_root], []));
        Assert.Equal("Дата файла", photo.DateSource);
        Assert.Equal(File.GetLastWriteTime(path), photo.TakenAt);
        Assert.Null(photo.Latitude); Assert.Null(photo.Longitude);
    }

    [Fact]
    public async Task Exif_And_Gps_Are_Read_From_Synthetic_Tiff()
    {
        Write("gps.tiff", Tiff());
        var photo = Assert.Single(await new PhotoScanner().ScanAsync([_root], []));
        Assert.Equal("EXIF", photo.DateSource);
        Assert.Equal(new DateTime(2020, 7, 8, 9, 10, 11), photo.TakenAt);
        Assert.Equal(55.75, photo.Latitude!.Value, 6);
        Assert.Equal(37.5, photo.Longitude!.Value, 6);
    }

    [Fact]
    public async Task Scan_And_Hash_Leave_Original_Bytes_And_Write_Creation_Times_Unchanged()
    {
        var a = Write("a.bmp", Bmp());
        var b = Write("nested/b.bmp", Bmp());
        var snapshots = new[] { a, b }.Select(p =>
            (Path: p, Bytes: File.ReadAllBytes(p), Write: File.GetLastWriteTimeUtc(p), Created: File.GetCreationTimeUtc(p))).ToArray();
        var photos = await new PhotoScanner().ScanAsync([_root, Path.Combine(_root, "nested")], []);
        Assert.Equal(2, photos.Count);
        Assert.Single(await new DuplicateFinder().FindAsync(photos));
        foreach (var s in snapshots)
        {
            Assert.Equal(s.Bytes, File.ReadAllBytes(s.Path));
            Assert.Equal(s.Write, File.GetLastWriteTimeUtc(s.Path));
            Assert.Equal(s.Created, File.GetCreationTimeUtc(s.Path));
        }
    }

    [Fact]
    public async Task Same_Size_Real_Images_Require_Equal_Content()
    {
        Write("a.bmp", Bmp()); Write("b.bmp", Bmp()); Write("c.bmp", Bmp(7));
        var photos = await new PhotoScanner().ScanAsync([_root], []);
        Assert.Single(photos.Select(p => p.Size).Distinct());
        var group = Assert.Single(await new DuplicateFinder().FindAsync(photos));
        Assert.Equal(new[] { "a.bmp", "b.bmp" }, group.Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task Valid_Zero_Gps_Is_Not_Treated_As_Missing()
    {
        var bytes = Tiff();
        foreach (var offset in new[] { 244, 252, 268, 276 })
            Array.Clear(bytes, offset, 4);
        Write("zero.tiff", bytes);
        var photo = Assert.Single(await new PhotoScanner().ScanAsync([_root], []));
        Assert.Equal(0d, photo.Latitude); Assert.Equal(0d, photo.Longitude);
    }

    [Fact]
    public async Task Exclusive_Locked_File_Is_Skipped_By_Hasher_Without_False_Group()
    {
        var path = Write("a.bmp", Bmp()); Write("b.bmp", Bmp());
        var photos = await new PhotoScanner().ScanAsync([_root], []);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var finder = new DuplicateFinder();
        Assert.Empty(await finder.FindAsync(photos));
        Assert.Equal(1, finder.WarningCount);
        Assert.Null(photos.Single(p => p.Path == path).Sha256);
    }

    [Fact]
    public void Disposing_Store_Releases_Database_File()
    {
        var path = Path.Combine(_root, "catalog.sqlite");
        using (var store = new CatalogStore(path)) store.AddSource(_root);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public async Task Sqlite_Roundtrip_Preserves_Metadata_Hashes_And_Sources()
    {
        Write("a.tiff", Tiff()); Write("b.tiff", Tiff());
        var photos = await new PhotoScanner().ScanAsync([_root], []);
        await new DuplicateFinder().FindAsync(photos);
        var db = Path.Combine(_root, "catalog.sqlite");
        using (var store = new CatalogStore(db))
        { store.AddSource(_root); store.AddSource(_root); store.Upsert(photos); }
        using var reopened = new CatalogStore(db);
        Assert.Single(reopened.GetSources());
        var records = reopened.GetAll();
        Assert.Equal(2, records.Count);
        Assert.All(records, p =>
        {
            Assert.Equal(photos[0].Sha256, p.Sha256);
            Assert.Equal("EXIF", p.DateSource);
            Assert.Equal(55.75, p.Latitude);
            Assert.Equal(photos[0].TakenAt, p.TakenAt);
        });
    }

    [Fact]
    public async Task Missing_Source_Does_Not_Clear_Persisted_Records()
    {
        Write("a.bmp", Bmp());
        var scanner = new PhotoScanner();
        using var store = new CatalogStore(Path.Combine(_root, "catalog.sqlite"));
        store.AddSource(_root);
        store.Upsert(await scanner.ScanAsync([_root], []));
        store.Upsert(await scanner.ScanAsync([Path.Combine(_root, "offline")], store.GetAll()));
        Assert.Single(store.GetAll()); Assert.Single(store.GetSources());
    }

    [Fact]
    public async Task Unavailable_Source_Is_Reported_Instead_Of_Silent_Success()
    {
        var scanner = new PhotoScanner();
        Assert.Empty(await scanner.ScanAsync([Path.Combine(_root, "missing")], []));
        Assert.Equal(1, scanner.WarningCount);
    }

    [Fact]
    public async Task Precancelled_Scan_And_Duplicate_Search_Do_Not_Report_Success()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PhotoScanner().ScanAsync([_root], [], cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DuplicateFinder().FindAsync([], cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Cancellation_During_Scan_Is_Observed()
    {
        for (var i = 0; i < 60; i++) Write($"{i}.bmp", Bmp());
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PhotoScanner().ScanAsync([_root], [], new CancelProgress(cts), cts.Token));
    }

    private sealed class CancelProgress(CancellationTokenSource cts) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => cts.Cancel();
    }

    // Little-endian TIFF: IFD0 -> EXIF DateTimeOriginal and GPS IFD, plus one RGB pixel.
    private static byte[] Tiff()
    {
        using var s = new MemoryStream(); using var w = new BinaryWriter(s);
        void Entry(ushort tag, ushort type, uint count, uint value)
        { w.Write(tag); w.Write(type); w.Write(count); w.Write(value); }
        w.Write((ushort)0x4949); w.Write((ushort)42); w.Write((uint)8);
        w.Write((ushort)11);
        Entry(256, 4, 1, 1); Entry(257, 4, 1, 1); Entry(258, 3, 3, 146);
        Entry(259, 3, 1, 1); Entry(262, 3, 1, 2); Entry(273, 4, 1, 300);
        Entry(277, 3, 1, 3); Entry(278, 4, 1, 1); Entry(279, 4, 1, 3);
        Entry(34665, 4, 1, 152); Entry(34853, 4, 1, 170); w.Write((uint)0);
        w.Write((ushort)8); w.Write((ushort)8); w.Write((ushort)8);
        w.Write((ushort)1); Entry(36867, 2, 20, 224); w.Write((uint)0);
        w.Write((ushort)4);
        Entry(1, 2, 2, 'N'); Entry(2, 5, 3, 244); Entry(3, 2, 2, 'E'); Entry(4, 5, 3, 268);
        w.Write((uint)0);
        w.Write(Encoding.ASCII.GetBytes("2020:07:08 09:10:11\0"));
        foreach (uint v in new uint[] { 55, 1, 45, 1, 0, 1, 37, 1, 30, 1, 0, 1 }) w.Write(v);
        while (s.Length < 300) w.Write((byte)0);
        w.Write(new byte[] { 1, 2, 3 }); return s.ToArray();
    }

    public void Dispose() => Directory.Delete(_root, true); // Only this test's own temporary data.
}
