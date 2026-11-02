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
    private readonly ThumbnailService? _service;
    private Task? _load;
    private int _request;
    private bool _selected;
    public PhotoRecord Photo { get; }
    public string Name => Photo.Name;
    public string SelectionName => $"Выделить: {Name}";
    public string Caption => PhotoPresentation.DateCaption(Photo);
    public string SizeLabel => Photo.Size >= 1024 * 1024 ? $"{Photo.Size / 1048576d:F1} МБ" : Photo.Size >= 1024 ? $"{Photo.Size / 1024d:F1} КБ" : $"{Photo.Size} Б";
    public string Resolution { get; private set; } = "Не определено";
    public bool PreviewFailed { get; private set; }
    public bool? SourceAvailable { get; private set; }
    public bool FromCache { get; private set; }
    public bool SourceChanged { get; private set; }
    public bool Persisted { get; private set; }
    public string? PreviewMessage { get; private set; }
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; Changed(nameof(IsSelected)); Changed(nameof(SelectedVisibility)); } }
    public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public int DuplicateCount { get; }
    public Visibility DuplicateVisibility => DuplicateCount > 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnavailableVisibility => SourceAvailable == false ? Visibility.Visible : Visibility.Collapsed;
    public BitmapSource? Thumbnail { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public PhotoTile(PhotoRecord photo, int duplicates = 0, ThumbnailService? service = null) { Photo = photo; DuplicateCount = duplicates; _service = service; }
    public Task LoadAsync(CancellationToken token, bool refresh = false)
    {
        if (_service is null || (!refresh && Thumbnail is not null)) return Task.CompletedTask;
        if (!refresh && _load is not null) return _load;
        int request = ++_request; return _load = LoadCoreAsync(request, token);
    }
    private async Task LoadCoreAsync(int request, CancellationToken token)
    {
        try
        {
            var result = await _service!.GetAsync(Photo, token); token.ThrowIfCancellationRequested();
            if (request != _request) return;
            Thumbnail = result.Bitmap; PreviewFailed = result.Bitmap is null; SourceAvailable = !result.State.IsOffline;
            FromCache = result.State.Persisted; Persisted = result.State.Persisted; SourceChanged = result.State.SourceChanged; PreviewMessage = result.State.Reason;
            if (result.State.Image is { } pixels) Resolution = $"{pixels.OriginalWidth:N0} × {pixels.OriginalHeight:N0}";
            foreach (var name in new[] { nameof(Thumbnail), nameof(Resolution), nameof(UnavailableVisibility), nameof(PreviewFailed), nameof(SourceAvailable) }) Changed(name);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.IO.FileFormatException or UnauthorizedAccessException or NotSupportedException or ArgumentException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        { if (request == _request && ex is not OperationCanceledException and not ObjectDisposedException) PreviewFailed = true; }
        finally { if (request == _request) _load = null; }
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
public sealed class LocationView(PhotoRecord photo, ThumbnailService? service = null)
{
    public PhotoRecord Photo { get; } = photo;
    public PhotoTile Tile { get; } = new(photo, service: service);
    public string Coordinates => string.Format(CultureInfo.InvariantCulture, "{0:F6}, {1:F6}", Photo.Latitude, Photo.Longitude);
}
public sealed class SourceView(string path, int count, bool included = true) : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim Probes = new(4);
    private bool? _available;
    private bool _included = included;
    public string FullPath { get; } = path;
    public string Name => System.IO.Path.GetFileName(FullPath.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : FullPath;
    public string CountLabel => $"Известных снимков: {count:N0}";
    public bool? Available => _available;
    public string Status => Available is true ? "● Доступен" : Available is false ? "○ Недоступен" : "Проверка…";
    public string VisibilityName => $"Показывать в библиотеке: {FullPath}";
    public bool IsIncluded { get => _included; set { if (value == _included) return; _included = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIncluded))); } }
    public Brush StatusBrush => new SolidColorBrush(Available is true ? Color.FromRgb(36, 141, 112) : Available is false ? Color.FromRgb(180, 133, 55) : Color.FromRgb(140, 150, 165));
    public event PropertyChangedEventHandler? PropertyChanged;
    public async Task RefreshAvailabilityAsync(CancellationToken token)
    {
        bool acquired = false;
        try
        {
            await Probes.WaitAsync(token); acquired = true;
            bool available = await Task.Run(() => Directory.Exists(FullPath), token); token.ThrowIfCancellationRequested();
            _available = available;
            foreach (var name in new[] { nameof(Available), nameof(Status), nameof(StatusBrush) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        catch (OperationCanceledException) { }
        finally { if (acquired) Probes.Release(); }
    }
}
