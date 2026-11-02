using Fotoarhiv.App;
using Fotoarhiv.Core;
using Xunit;

namespace Fotoarhiv.Tests;

public sealed class PresentationTests
{
    [Fact]
    public void Timeline_Groups_Real_Dates_By_Month_In_Descending_Order()
    {
        var old = new PhotoRecord { Path = "old.jpg", TakenAt = new DateTime(2023, 8, 2), DateSource = "EXIF" };
        var fallback = new PhotoRecord { Path = "file.jpg", TakenAt = new DateTime(2024, 7, 1), DateSource = "Дата файла" };
        var recent = new PhotoRecord { Path = "new.jpg", TakenAt = new DateTime(2024, 7, 18), DateSource = "EXIF" };
        var groups = PhotoPresentation.GroupMonths([old, fallback, recent]);
        Assert.Equal(2, groups.Count);
        Assert.Equal("Июль 2024", groups[0].Title);
        Assert.Equal(new[] { recent, fallback }, groups[0].Photos);
        Assert.Equal("Август 2023", groups[1].Title);
        Assert.Equal("Дата файла", fallback.DateSource);
    }

    [Fact]
    public void Statistics_Use_Catalog_Values_And_Mark_Unchecked_Duplicates_Unknown()
    {
        var a = new PhotoRecord { Latitude = 55.75, Longitude = 37.5 };
        var b = new PhotoRecord { Latitude = 55.75, Longitude = 37.5 };
        var c = new PhotoRecord();
        var unknown = PhotoPresentation.Statistics([a, b, c], null, 2);
        Assert.Equal(3, unknown.Photos);
        Assert.Equal(1, unknown.Places);
        Assert.Equal(2, unknown.Sources);
        Assert.Null(unknown.DuplicatePhotos);
        var known = PhotoPresentation.Statistics([a, b, c], [[a, b]], 2);
        Assert.Equal(2, known.DuplicatePhotos);
    }

    [Fact]
    public void Source_Counts_Respect_Directory_Boundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "photos");
        Assert.True(PhotoPresentation.BelongsToSource(Path.Combine(root, "nested", "a.jpg"), root));
        Assert.False(PhotoPresentation.BelongsToSource(Path.Combine(root + "-other", "a.jpg"), root));
    }

    [Fact]
    public void Fallback_Date_Is_Labelled_And_Does_Not_Invent_A_Location()
    {
        var record = new PhotoRecord { TakenAt = new DateTime(2024, 7, 18), DateSource = "Дата файла" };
        var caption = PhotoPresentation.DateCaption(record);
        Assert.Contains("18", caption);
        Assert.Contains("2024", caption);
        Assert.Contains("Дата файла", caption);
        Assert.DoesNotContain("Сочи", caption);
        record.DateSource = "EXIF";
        Assert.DoesNotContain("Дата файла", PhotoPresentation.DateCaption(record));
    }

    [Fact]
    public void Gps_Count_Requires_Valid_Pairs_And_Allows_Zero_Coordinates()
    {
        var records = new[]
        {
            new PhotoRecord { Latitude = 0, Longitude = 0 },
            new PhotoRecord { Latitude = 55 },
            new PhotoRecord { Latitude = double.NaN, Longitude = 37 },
            new PhotoRecord { Latitude = 91, Longitude = 37 }
        };
        Assert.Equal(1, PhotoPresentation.Statistics(records, [], 0).Places);
    }

    [Fact]
    public void Empty_Catalog_Produces_No_Demo_Data()
    {
        Assert.Empty(PhotoPresentation.GroupMonths([]));
        var statistics = PhotoPresentation.Statistics([], [], 0);
        Assert.Equal(0, statistics.Photos);
        Assert.Equal(0, statistics.Places);
        Assert.Equal(0, statistics.DuplicatePhotos);
        Assert.Equal(0, statistics.Sources);
    }
}
