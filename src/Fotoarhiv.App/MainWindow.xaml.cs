using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fotoarhiv.Core;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fotoarhiv.App;

public partial class MainWindow : Window
{
    private readonly CatalogStore _store;
    private readonly PhotoScanner _scanner = new();
    private readonly DuplicateFinder _duplicateFinder = new();
    private List<PhotoRecord> _records = [];
    private List<DuplicateGroupView> _duplicateGroups = [];
    private CancellationTokenSource? _scanCancellation;
    private int _page;
    private const int PageSize = 72;
    private bool _initialized;
    private bool _updatingYears;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fotoarhiv");
        _store = new CatalogStore(System.IO.Path.Combine(folder, "catalog.sqlite"));
        _records = _store.GetAll();
        UpdateSources();
        _initialized = true;
        UpdateYearOptions();
        SetPage("photos");
        UpdatePlaces();
    }

    private void UpdateSources() => SourcesList.ItemsSource = _store.GetSources();

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        using var picker = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выбери папку с фотографиями",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try { _store.AddSource(picker.SelectedPath); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось добавить папку"); return; }
        UpdateSources();
        await ScanNowAsync();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) { _scanCancellation.Cancel(); return; }
        await ScanNowAsync();
    }

    private async Task ScanNowAsync()
    {
        if (_scanCancellation is not null) return;
        var roots = _store.GetSources();
        if (roots.Count == 0)
        {
            Status.Text = "Сначала добавьте папку с фотографиями";
            return;
        }
        using var cts = new CancellationTokenSource();
        _scanCancellation = cts;
        ScanAction.Content = "Остановить";
        AddFolderAction.IsEnabled = false;
        _duplicateGroups = [];
        DuplicateGroups.ItemsSource = _duplicateGroups;
        DuplicateMembers.ItemsSource = null;
        Status.Text = "Анализ фотографий… файлы не изменяются";
        var progress = new Progress<ScanProgress>(p => { if (!_closing && ReferenceEquals(_scanCancellation, cts)) Status.Text = $"Просмотрено фотографий: {p.Scanned:N0}"; });
        try
        {
            var scanned = await _scanner.ScanAsync(roots, _records, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_closing) return;
            _store.Upsert(scanned);
            _records = _store.GetAll();
            Status.Text = "Поиск точных дубликатов по содержимому…";
            // File enumeration/validation and hashing must not run on the dispatcher.
            var records = _records;
            var duplicates = await Task.Run(() => _duplicateFinder.FindAsync(records, cancellationToken: cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_closing) return;
            _store.Upsert(_records); // retain computed SHA-256 for unchanged records
            _duplicateGroups = duplicates.Select((group, index) =>
                new DuplicateGroupView($"Группа {index + 1} · {group.Count} файла", group)).ToList();
            DuplicateGroups.ItemsSource = _duplicateGroups;
            DuplicateMembers.ItemsSource = null;
            var warnings = _scanner.WarningCount + _duplicateFinder.WarningCount;
            Status.Text = warnings == 0
                ? $"Готово: {_records.Count:N0} файлов, {_duplicateGroups.Count:N0} групп точных дублей"
                : $"Проверка неполная: недоступно или изменилось объектов: {warnings:N0}. Записи сохранены. Повторите обновление.";
        }
        catch (OperationCanceledException)
        {
            if (_closing) return;
            Status.Text = "Остановлено пользователем. Исходные файлы не изменены.";
        }
        catch (Exception ex)
        {
            if (_closing) return;
            Status.Text = "Ошибка сканирования";
            MessageBox.Show(ex.Message, "Фотоархив", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _scanCancellation = null;
            if (!_closing)
            {
                ScanAction.Content = "Обновить";
                AddFolderAction.IsEnabled = true;
                UpdateYearOptions();
                RefreshGallery();
                UpdatePlaces();
            }
        }
    }

    private void UpdateYearOptions()
    {
        _updatingYears = true;
        var previous = YearPicker.SelectedItem as string ?? "Все годы";
        var choices = new List<string> { "Все годы" };
        choices.AddRange(_records.Select(r => r.TakenAt.Year).Where(y => y > 1800).Distinct()
            .OrderByDescending(y => y).Select(y => y.ToString(CultureInfo.InvariantCulture)));
        YearPicker.ItemsSource = choices;
        YearPicker.SelectedItem = choices.Contains(previous) ? previous : "Все годы";
        _updatingYears = false;
    }

    private void RefreshGallery()
    {
        if (!_initialized) return;
        IEnumerable<PhotoRecord> filtered = _records;
        var keyword = SearchBox.Text.Trim();
        if (keyword.Length > 0)
            filtered = filtered.Where(p => p.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        var year = YearPicker.SelectedItem as string;
        if (int.TryParse(year, CultureInfo.InvariantCulture, out var y))
            filtered = filtered.Where(p => p.TakenAt.Year == y);
        var selection = filtered.OrderByDescending(p => p.TakenAt).ToList();
        var totalPages = Math.Max(1, (int)Math.Ceiling(selection.Count / (double)PageSize));
        _page = Math.Clamp(_page, 0, totalPages - 1);
        // Only 72 bounded previews at once: keeps large collections usable.
        PhotosGallery.ItemsSource = selection.Skip(_page * PageSize).Take(PageSize)
            .Select(r => new PhotoTile(r)).ToList();
        PageCount.Text = $"{_page + 1} / {totalPages}";
        Summary.Text = $"{_records.Count:N0} в библиотеке · {selection.Count:N0} по фильтру";
    }

    private void UpdatePlaces()
    {
        PlacesList.ItemsSource = _records.Where(p => p.Latitude.HasValue && p.Longitude.HasValue)
            .OrderByDescending(p => p.TakenAt).Select(p => new LocationView(p)).ToList();
    }

    private void SetPage(string page)
    {
        PhotosPanel.Visibility = page == "photos" ? Visibility.Visible : Visibility.Collapsed;
        PlacesPanel.Visibility = page == "places" ? Visibility.Visible : Visibility.Collapsed;
        DuplicatesPanel.Visibility = page == "duplicates" ? Visibility.Visible : Visibility.Collapsed;
        SourcesPanel.Visibility = page == "sources" ? Visibility.Visible : Visibility.Collapsed;
        Filters.Visibility = page == "photos" ? Visibility.Visible : Visibility.Collapsed;
        Pages.Visibility = page == "photos" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch
        {
            "places" => "Места съёмки",
            "duplicates" => "Точные повторы",
            "sources" => "Источники фотографий",
            _ => "Все фотографии"
        };
        foreach (var button in new[] { NavPhotos, NavPlaces, NavDuplicates, NavSources })
        {
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22344E"));
        }
        var selected = page switch
        {
            "places" => NavPlaces,
            "duplicates" => NavDuplicates,
            "sources" => NavSources,
            _ => NavPhotos
        };
        selected.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E4669"));
        if (page == "photos") RefreshGallery();
    }

    private void NavigatePhotos_Click(object sender, RoutedEventArgs e) => SetPage("photos");
    private void NavigatePlaces_Click(object sender, RoutedEventArgs e) => SetPage("places");
    private void NavigateDuplicates_Click(object sender, RoutedEventArgs e) => SetPage("duplicates");
    private void NavigateSources_Click(object sender, RoutedEventArgs e) => SetPage("sources");
    private void Search_Changed(object sender, TextChangedEventArgs e) { _page = 0; RefreshGallery(); }
    private void Year_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingYears) return;
        _page = 0;
        RefreshGallery();
    }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) { _page = Math.Max(0, _page - 1); RefreshGallery(); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { _page++; RefreshGallery(); }

    private void Photo_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not PhotoTile tile) return;
        var p = tile.Photo;
        MessageBox.Show($"{p.Name}\n\nДата: {p.TakenAt:dd.MM.yyyy HH:mm}\nИсточник даты: {p.DateSource}\n" +
            $"Размер: {p.Size / 1024d / 1024d:F2} МБ\n" +
            $"GPS: {(p.Latitude.HasValue ? $"{p.Latitude:F6}, {p.Longitude:F6}" : "нет данных")}\n\n{p.Path}",
            "Сведения о фотографии", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DuplicateGroup_Changed(object sender, SelectionChangedEventArgs e)
    {
        DuplicateMembers.ItemsSource = (DuplicateGroups.SelectedItem as DuplicateGroupView)?.Photos;
    }

    private void OpenMap_Click(object sender, RoutedEventArgs e)
    {
        if (PlacesList.SelectedItem is not LocationView loc)
        { Status.Text = "Выберите снимок с GPS в списке"; return; }
        var lat = loc.Photo.Latitude!.Value.ToString("F6", CultureInfo.InvariantCulture);
        var lon = loc.Photo.Longitude!.Value.ToString("F6", CultureInfo.InvariantCulture);
        var url = $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=15/{lat}/{lon}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось открыть карту"); }
    }

    private void ExportPlaceholder_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Режимы «Копировать» и «Переместить» добавим после проверки сканирования и поиска дублей. " +
            "В этой версии приложение не умеет изменять или переносить фотографии.",
            "Безопасность фотоархива", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _scanCancellation?.Cancel();
        _store.Dispose();
        base.OnClosed(e);
    }
}

public sealed class PhotoTile
{
    public PhotoRecord Photo { get; }
    public string Name => Photo.Name;
    public string DateLabel => Photo.DateSource == "EXIF" ? "EXIF" : "Дата файла";
    public string Caption => $"{Photo.TakenAt:dd MMM yyyy} · {(Photo.IsAvailable ? "доступно" : "источник недоступен")}";
    public BitmapSource? Thumbnail { get; }

    public PhotoTile(PhotoRecord photo)
    {
        Photo = photo;
        if (!photo.IsAvailable) return;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(photo.Path, UriKind.Absolute);
            bitmap.DecodePixelWidth = 240;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            Thumbnail = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.IO.FileFormatException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Unsupported RAW/HEIC codecs remain in the catalog; placeholder appears instead.
        }
    }
}

public sealed record DuplicateGroupView(string Title, List<PhotoRecord> Photos);
public sealed class LocationView(PhotoRecord photo)
{
    public PhotoRecord Photo { get; } = photo;
    public string Description => $"{Photo.TakenAt:dd.MM.yyyy} · {Photo.Name} · {Photo.Latitude:F4}, {Photo.Longitude:F4}";
}
