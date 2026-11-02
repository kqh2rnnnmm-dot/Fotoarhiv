using Fotoarhiv.App;
using Fotoarhiv.Core;
using Xunit;

namespace Fotoarhiv.Tests;

public sealed class P1ViewTests
{
    private static PhotoRecord Photo(string path, int year, int month = 1, int day = 1) => new() { Path = path, TakenAt = new DateTime(year, month, day), DateSource = "EXIF" };

    [Theory]
    [InlineData(@"C:\Photos\nested\a.jpg", @"c:/PHOTOS/", true)]
    [InlineData(@"C:\PhotosOld\a.jpg", @"C:\Photos", false)]
    [InlineData(@"\\server\share\Photos\a.jpg", @"\\SERVER\SHARE\Photos\", true)]
    [InlineData(@"\\server\share\Photos2\a.jpg", @"\\server\share\Photos", false)]
    [InlineData(@"\\?\C:\Photos\a.jpg", @"c:\Photos", true)]
    public void Source_Membership_Uses_Windows_Directory_Boundaries(string file, string root, bool expected) => Assert.Equal(expected, PathIdentity.BelongsTo(file, root));

    [Fact]
    public void Both_Directions_Order_Months_And_Photos_With_Stable_Ties()
    {
        var records = new[] { Photo(@"C:\P\z.jpg", 2024, 7, 18), Photo(@"C:\P\a.jpg", 2024, 7, 18), Photo(@"C:\P\old.jpg", 2020) };
        var view = new LibraryViewState(records, [@"C:\P"]);
        Assert.Equal(new[] { records[1], records[0], records[2] }, view.Photos);
        view.SetSort(SortDirection.OldestFirst);
        Assert.Equal(new[] { records[2], records[1], records[0] }, view.Photos);
        var months = PhotoPresentation.GroupMonths(view.Photos, oldestFirst: true);
        Assert.Equal(new[] { "Январь 2020", "Июль 2024" }, months.Select(g => g.Title));
        Assert.Equal(new[] { records[1], records[0] }, months[1].Photos);
    }

    [Fact]
    public void Hiding_Overlapping_Sources_Is_A_Union_And_Does_Not_Mutate_Records()
    {
        var nested = Photo(@"C:\Photos\nested\a.jpg", 2024);
        var root = Photo(@"C:\Photos\b.jpg", 2023);
        var other = Photo(@"C:\PhotosOld\c.jpg", 2022);
        nested.Sha256 = "existing hash"; nested.Latitude = 1; nested.Longitude = 2;
        var view = new LibraryViewState([nested, root, nested, other], [@"C:\Photos", @"C:\Photos\nested", @"C:\PhotosOld"]);
        Assert.Equal(3, view.Photos.Count);
        view.SetSourceVisible(@"c:\PHOTOS\", false);
        Assert.Equal(new[] { nested, other }, view.Photos);
        view.HideAllSources(); Assert.Empty(view.Photos);
        view.ShowAllSources(); Assert.Equal(3, view.Photos.Count);
        Assert.Equal("existing hash", nested.Sha256); Assert.Equal(1, nested.Latitude); Assert.Equal(2, nested.Longitude);
        Assert.Equal(3, view.Sources.Count);
    }

    [Fact]
    public void Duplicates_Are_Projected_Without_Destroying_The_Original_Group()
    {
        var a = Photo(@"C:\A\same-a.jpg", 2024); var b = Photo(@"C:\B\same-b.jpg", 2024);
        IReadOnlyList<PhotoRecord> group = new[] { a, b };
        var view = new LibraryViewState(group, [@"C:\A", @"C:\B"]);
        Assert.Single(view.VisibleDuplicates([group]));
        view.SetSourceVisible(@"C:\B", false); Assert.Empty(view.VisibleDuplicates([group]));
        view.ShowAllSources(); Assert.Single(view.VisibleDuplicates([group]));
        Assert.Equal(2, group.Count);
    }

    [Fact]
    public void Search_Year_And_Sources_Compose_With_Oldest_Order()
    {
        var a = Photo(@"C:\A\match.jpg", 2020); var b = Photo(@"C:\B\match.jpg", 2024); var c = Photo(@"C:\A\other.jpg", 2020);
        var view = new LibraryViewState([b, c, a], [@"C:\A", @"C:\B"]);
        view.SetSort(SortDirection.OldestFirst); view.SetSearch("MATCH");
        Assert.Equal(new[] { a, b }, view.Photos);
        view.SetYear(2020); Assert.Equal(new[] { a }, view.Photos);
        view.SetSourceVisible(@"C:\A", false); Assert.Empty(view.Photos);
    }

    [Fact]
    public void Selection_Crosses_Pages_And_Ctrl_Shift_Uses_The_Whole_Filtered_Order()
    {
        var records = Enumerable.Range(0, 100).Select(i => Photo($@"C:\A\{i:000}.jpg", 2024)).ToArray();
        var view = new LibraryViewState(records, [@"C:\A"]);
        view.Selection.Toggle(records[1].Path); view.Selection.Toggle(records[73].Path);
        Assert.Equal(2, view.Selection.Count);
        view.Selection.SelectRange(records[76].Path, view.Photos, additive: true);
        Assert.Equal(5, view.Selection.Count);
        Assert.True(view.Selection.Contains(records[1].Path));
        Assert.True(view.Selection.Contains(records[74].Path));
        view.Selection.Clear(); Assert.Equal(0, view.Selection.Count);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("year")]
    [InlineData("sort")]
    [InlineData("sources")]
    public void A_Changed_Filter_Clears_Selection(string change)
    {
        var photo = Photo(@"C:\A\a.jpg", 2024); var view = new LibraryViewState([photo], [@"C:\A"]);
        view.Selection.Toggle(photo.Path);
        switch (change)
        {
            case "search": view.SetSearch("a"); break;
            case "year": view.SetYear(2024); break;
            case "sort": view.SetSort(SortDirection.OldestFirst); break;
            case "sources": view.HideAllSources(); break;
        }
        Assert.Equal(0, view.Selection.Count);
    }

    [Fact]
    public void Viewer_Navigates_Only_The_Current_Ordered_Snapshot_Without_Wrapping()
    {
        var records = new[] { Photo(@"C:\A\1.jpg", 2020), Photo(@"C:\A\2.jpg", 2021), Photo(@"C:\B\3.jpg", 2024) };
        var view = new LibraryViewState(records, [@"C:\A", @"C:\B"]);
        view.SetSourceVisible(@"C:\B", false); view.SetSort(SortDirection.OldestFirst);
        var viewer = new ViewerSequence(view.Photos, records[0].Path);
        Assert.False(viewer.Move(-1)); Assert.True(viewer.Move(1)); Assert.Same(records[1], viewer.Current); Assert.False(viewer.Move(1));
        Assert.DoesNotContain(records[2], viewer.Photos);
    }

    [Fact]
    public async Task Sort_Persists_But_Source_Visibility_Does_Not()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Fotoarhiv-P1-settings-" + Guid.NewGuid());
        try
        {
            var settings = new SortPreferenceStore(folder); Assert.Equal(SortDirection.NewestFirst, settings.Load());
            await settings.SaveAsync(SortDirection.OldestFirst);
            Assert.Equal(SortDirection.OldestFirst, new SortPreferenceStore(folder).Load());
            var view = new LibraryViewState([Photo(@"C:\A\1.jpg", 2020)], [@"C:\A"]); view.HideAllSources();
            Assert.Single(new LibraryViewState(view.Catalog, view.Sources).Photos);
            var body = await File.ReadAllTextAsync(Path.Combine(folder, "settings.json"));
            Assert.DoesNotContain("C:\\A", body); Assert.DoesNotContain("hidden", body, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(Path.Combine(folder, "settings.json"), "corrupt settings");
            Assert.Equal(SortDirection.NewestFirst, settings.Load());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
