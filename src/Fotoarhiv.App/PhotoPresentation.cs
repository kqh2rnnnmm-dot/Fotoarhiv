using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Fotoarhiv.Core;

namespace Fotoarhiv.App;

// Pure presentation calculations. They neither read metadata nor mutate the catalog.
public static class PhotoPresentation
{
    public static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static List<PhotoMonth> GroupMonths(IEnumerable<PhotoRecord> photos) => photos
        .OrderByDescending(p => p.TakenAt).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
        .GroupBy(p => new DateTime(p.TakenAt.Year, p.TakenAt.Month, 1))
        .Select(g => new PhotoMonth(g.Key,
            Russian.TextInfo.ToTitleCase(g.Key.ToString("MMMM yyyy", Russian)), g.ToList())).ToList();

    public static LibraryStatistics Statistics(IEnumerable<PhotoRecord> records,
        IEnumerable<IEnumerable<PhotoRecord>>? duplicates, int sources)
    {
        var photos = records.ToList();
        return new LibraryStatistics(photos.Count,
            duplicates?.Sum(group => group.Count()),
            photos.Where(HasGps).Select(p => (p.Latitude, p.Longitude)).Distinct().Count(), sources);
    }

    public static bool HasGps(PhotoRecord photo) => photo.Latitude is { } lat && photo.Longitude is { } lon &&
        double.IsFinite(lat) && double.IsFinite(lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;

    public static string DateCaption(PhotoRecord photo) => photo.TakenAt.ToString("dd MMMM yyyy", Russian) +
        (photo.DateSource == "EXIF" ? "" : " · Дата файла");

    public static bool BelongsToSource(string file, string root) => Path.GetFullPath(file)
        .StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed record PhotoMonth(DateTime Month, string Title, List<PhotoRecord> Photos);
public sealed record LibraryStatistics(int Photos, int? DuplicatePhotos, int Places, int Sources);
