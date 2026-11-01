using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Fotoarhiv.Core;

/// <summary>Non-destructive read-only scanner. Unavailable sources never cause catalog deletion.</summary>
public sealed class PhotoScanner
{
    public int WarningCount { get; private set; }

    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif", ".webp", ".heic", ".heif", ".avif", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2" };

    public Task<List<PhotoRecord>> ScanAsync(
        IEnumerable<string> roots, IEnumerable<PhotoRecord> previous,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // Keep directory traversal and metadata parsing off the caller's UI thread.
        return Task.Run(() => Scan(roots, previous, progress, cancellationToken), cancellationToken);
    }

    private List<PhotoRecord> Scan(IEnumerable<string> roots, IEnumerable<PhotoRecord> previous,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        WarningCount = 0;
        ct.ThrowIfCancellationRequested();
        var existing = previous.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, PhotoRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumeratePaths(roots, ct, () => WarningCount++))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { WarningCount++; continue; }
                if (existing.TryGetValue(path, out var old) && old.Size == info.Length &&
                    old.ModifiedUtcTicks == info.LastWriteTimeUtc.Ticks)
                {
                    found[path] = old;
                }
                else
                {
                    found[path] = ReadRecord(info);
                }
            }
            catch (IOException) { WarningCount++; }
            catch (UnauthorizedAccessException) { WarningCount++; }
            if (found.Count % 50 == 0) progress?.Report(new ScanProgress(found.Count, path));
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress(found.Count, "Готово"));
        return found.Values.ToList();
    }

    private static IEnumerable<string> EnumeratePaths(IEnumerable<string> roots, CancellationToken ct, Action warn)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new Stack<string>();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (System.IO.Directory.Exists(root)) dirs.Push(System.IO.Path.GetFullPath(root));
            else warn();
        }
        while (dirs.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = dirs.Pop();
            if (!visited.Add(dir)) continue;
            string[] children;
            string[] files;
            try
            {
                children = System.IO.Directory.GetDirectories(dir);
                files = System.IO.Directory.GetFiles(dir);
            }
            catch (IOException) { warn(); continue; }
            catch (UnauthorizedAccessException) { warn(); continue; }
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        dirs.Push(child);
                }
                catch (IOException) { warn(); }
                catch (UnauthorizedAccessException) { warn(); }
            }
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (Extensions.Contains(System.IO.Path.GetExtension(file))) yield return file;
            }
        }
    }

    private static PhotoRecord ReadRecord(FileInfo file)
    {
        // A file's modification time is a fallback, not proof of when the picture was taken.
        var record = new PhotoRecord
        {
            Path = file.FullName,
            Size = file.Length,
            ModifiedUtcTicks = file.LastWriteTimeUtc.Ticks,
            TakenAt = file.LastWriteTime,
            DateSource = "Дата файла"
        };
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(file.FullName);
            var exif = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (exif is not null && exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var date) &&
                date.Year is >= 1900 and <= 2100)
            {
                record.TakenAt = date;
                record.DateSource = "EXIF";
            }
            if (dirs.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation() is { } gps &&
                double.IsFinite(gps.Latitude) && double.IsFinite(gps.Longitude) &&
                Math.Abs(gps.Latitude) <= 90 && Math.Abs(gps.Longitude) <= 180)
            {
                record.Latitude = gps.Latitude;
                record.Longitude = gps.Longitude;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ImageProcessingException or NotSupportedException)
        {
            // Unsupported/corrupt codec: still index the file, never silently drop it.
        }
        return record;
    }
}
