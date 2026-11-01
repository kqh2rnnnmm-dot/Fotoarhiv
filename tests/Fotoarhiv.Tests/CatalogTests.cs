using Fotoarhiv.Core;
using Xunit;

namespace Fotoarhiv.Tests;

public sealed class CatalogTests
{
    [Fact]
    public async Task Scan_Indexes_Photos_Without_Modifying_Source()
    {
        using var fixture = new PhotoFixture();
        var path = fixture.Create("a.jpg", "test-content");
        var original = File.ReadAllBytes(path);
        var originalWrite = File.GetLastWriteTimeUtc(path);

        var scanner = new PhotoScanner();
        var photos = await scanner.ScanAsync([fixture.Path], []);

        Assert.Single(photos);
        Assert.Equal(Path.GetFullPath(path), photos[0].Path);
        Assert.Equal("Дата файла", photos[0].DateSource);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(originalWrite, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Exact_Duplicates_Require_Same_Content_Not_Just_Same_Size()
    {
        using var fixture = new PhotoFixture();
        fixture.Create("A.jpg", "12345");
        fixture.Create("B.jpg", "12345");
        fixture.Create("C.jpg", "ABCDE");
        var photos = await new PhotoScanner().ScanAsync([fixture.Path], []);

        var groups = await new DuplicateFinder().FindAsync(photos);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Count);
        Assert.Contains(group, p => p.Name == "A.jpg");
        Assert.Contains(group, p => p.Name == "B.jpg");
    }

    [Fact]
    public async Task Catalog_Persists_And_Retains_Entries_For_Offline_Disks()
    {
        using var fixture = new PhotoFixture();
        var path = fixture.Create("summer.png", "not-a-real-image");
        using (var store = new CatalogStore(System.IO.Path.Combine(fixture.Path, "catalog.sqlite")))
        {
            store.AddSource(fixture.Path);
            var photos = await new PhotoScanner().ScanAsync([fixture.Path], []);
            store.Upsert(photos);
        }
        File.Delete(path);
        using var reopened = new CatalogStore(System.IO.Path.Combine(fixture.Path, "catalog.sqlite"));
        Assert.Single(reopened.GetAll());
        Assert.Single(reopened.GetSources());
    }

    [Fact]
    public async Task Rescan_Reuses_Unchanged_Record_And_Preserves_Hash()
    {
        using var fixture = new PhotoFixture();
        fixture.Create("1.jpg", "same-image");
        var scanner = new PhotoScanner();
        var photos = await scanner.ScanAsync([fixture.Path], []);
        photos[0].Sha256 = "previously-calculated-hash";

        var rescanned = await scanner.ScanAsync([fixture.Path], photos);

        Assert.Single(rescanned);
        Assert.Equal("previously-calculated-hash", rescanned[0].Sha256);
    }

    private sealed class PhotoFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FotoarhivTests-" + Guid.NewGuid().ToString("N"));
        public PhotoFixture() => System.IO.Directory.CreateDirectory(Path);
        public string Create(string name, string content)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllText(file, content);
            return file;
        }
        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }
}
