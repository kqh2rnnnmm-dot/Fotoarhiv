using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotoarhiv.Core;

namespace Fotoarhiv.App;

public sealed class PhotoTile : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim Decoders = new(4);
    private Task? _load;
    public PhotoRecord Photo { get; }
    public string Name => Photo.Name;
    public string Caption => PhotoPresentation.DateCaption(Photo);
    public string SizeLabel => Photo.Size >= 1024 * 1024 ? $"{Photo.Size / 1048576d:F1} МБ" : Photo.Size >= 1024 ? $"{Photo.Size / 1024d:F1} КБ" : $"{Photo.Size} Б";
    public string Resolution { get; private set; } = "Не определено";
    public bool PreviewFailed { get; private set; }
    public int DuplicateCount { get; }
    public Visibility DuplicateVisibility => DuplicateCount > 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnavailableVisibility => Photo.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
    public BitmapSource? Thumbnail { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public PhotoTile(PhotoRecord photo, int duplicates = 0) { Photo = photo; DuplicateCount = duplicates; }
    public Task LoadAsync(CancellationToken token) => Thumbnail is not null ? Task.CompletedTask : _load ??= LoadCoreAsync(token);
    private async Task LoadCoreAsync(CancellationToken token)
    {
        bool acquired = false;
        try
        {
            await Decoders.WaitAsync(token); acquired = true;
            var result = await Task.Run(() => ReadPreview(Photo.Path), token);
            token.ThrowIfCancellationRequested();
            Thumbnail = result.Image; Resolution = result.Resolution;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Resolution)));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.IO.FileFormatException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        { if (ex is not OperationCanceledException) PreviewFailed = true; /* Catalog records are preserved. */ }
        finally { if (acquired) Decoders.Release(); _load = null; }
    }
    private static (BitmapSource Image, string Resolution) ReadPreview(string path)
    {
        int width, height;
        // Read headers without materializing the full-resolution pixel buffer.
        using (var header = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = decoder.Frames[0].PixelWidth; height = decoder.Frames[0].PixelHeight;
        }
        double scale = Math.Min(1, 512d / Math.Max(width, height));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.StreamSource = stream;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Max(1, (int)(width * scale));
        bitmap.DecodePixelHeight = Math.Max(1, (int)(height * scale));
        bitmap.EndInit(); bitmap.Freeze();
        return (bitmap, $"{width:N0} × {height:N0}");
    }
}

public sealed record MonthView(string Title, string CountLabel, List<PhotoTile> Tiles);
public sealed record StatisticView(string Icon, string Value, string Label, string AutomationId)
{
    public override string ToString() => $"{Label}: {Value}";
}
public sealed record MetadataView(string Label, string Value);
public sealed record DuplicateGroupView(string Title, List<PhotoRecord> Photos)
{
    public IReadOnlyList<PhotoTile> PreviewPhotos { get; init; } = [];
    public string Footer => $"Файлов: {Photos.Count:N0} · без изменений";
    public string CompareName => $"Сравнить: {Title}";
}
public sealed class LocationView(PhotoRecord photo)
{
    public PhotoRecord Photo { get; } = photo;
    public PhotoTile Tile { get; } = new(photo);
    public string Coordinates => string.Format(CultureInfo.InvariantCulture, "{0:F6}, {1:F6}", Photo.Latitude, Photo.Longitude);
}
public sealed class SourceView(string path, int count)
{
    public string FullPath { get; } = path;
    public string Name => System.IO.Path.GetFileName(FullPath.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : FullPath;
    public string CountLabel => $"Известных снимков: {count:N0}";
    public bool Available { get; } = Directory.Exists(path);
    public string Status => Available ? "● Доступен" : "○ Недоступен";
    public Brush StatusBrush => new SolidColorBrush(Available ? Color.FromRgb(36, 141, 112) : Color.FromRgb(180, 133, 55));
}
