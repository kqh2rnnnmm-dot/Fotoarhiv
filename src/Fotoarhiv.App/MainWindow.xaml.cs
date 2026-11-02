using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Fotoarhiv.Core;

namespace Fotoarhiv.App;

public partial class MainWindow : Window
{
    private readonly CatalogStore _store;
    private readonly PhotoScanner _scanner = new();
    private readonly DuplicateFinder _duplicateFinder = new();
    private List<PhotoRecord> _records = [];
    private List<DuplicateGroupView> _duplicateGroups = [];
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource _previews = new();
    private CancellationTokenSource _dupePreviews = new();
    private readonly CancellationTokenSource _windowPreviews = new();
    private int _page, _dupePage, _filteredCount;
    private const int PageSize = 72, DuplicatePageSize = 12;
    private bool _initialized, _updatingYears, _closing, _duplicatesChecked;
    private string _currentPage = "photos";
    private PhotoRecord? _selectedPhoto;
    private IInputElement? _returnFocus;
    public static readonly DependencyProperty PhotoColumnsProperty = DependencyProperty.Register(nameof(PhotoColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(5));
    public static readonly DependencyProperty StatsColumnsProperty = DependencyProperty.Register(nameof(StatsColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(4));
    public static readonly DependencyProperty DupeColumnsProperty = DependencyProperty.Register(nameof(DupeColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(2));
    public int PhotoColumns { get => (int)GetValue(PhotoColumnsProperty); set => SetValue(PhotoColumnsProperty, value); }
    public int StatsColumns { get => (int)GetValue(StatsColumnsProperty); set => SetValue(StatsColumnsProperty, value); }
    public int DupeColumns { get => (int)GetValue(DupeColumnsProperty); set => SetValue(DupeColumnsProperty, value); }

    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fotoarhiv");
        _store = new CatalogStore(Path.Combine(folder, "catalog.sqlite"));
        _records = _store.GetAll();
        _initialized = true;
        UpdateYearOptions(); UpdateSources(); UpdateStatistics(); UpdatePlaces();
        SetPage("photos"); ApplyResponsiveLayout();
    }

    private void UpdateSources()
    {
        var paths = _store.GetSources();
        var sources = paths.Select(path => new SourceView(path, _records.Count(p => PhotoPresentation.BelongsToSource(p.Path, path)))).ToList();
        SourcesList.ItemsSource = sources; SidebarSources.ItemsSource = sources.Take(3).ToList();
        NoSidebarSources.Visibility = NoSourcesText.Visibility = sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = "Выбери папку с фотографиями", ShowNewFolderButton = false, UseDescriptionForTitle = true };
        if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try { _store.AddSource(picker.SelectedPath); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось добавить папку"); return; }
        UpdateSources(); await ScanNowAsync();
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
        if (roots.Count == 0) { Status.Text = "Сначала добавьте папку с фотографиями"; return; }
        using var cts = new CancellationTokenSource(); _scanCancellation = cts;
        ScanAction.Content = "Остановить"; AddFolderAction.IsEnabled = false;
        _duplicateGroups = []; _duplicatesChecked = false; _dupePage = 0;
        RefreshDuplicateCards(); UpdateStatistics();
        Status.Text = "Анализ фотографий… файлы не изменяются";
        var progress = new Progress<ScanProgress>(p => { if (!_closing && ReferenceEquals(_scanCancellation, cts)) Status.Text = $"Просмотрено фотографий: {p.Scanned:N0}"; });
        try
        {
            var scanned = await _scanner.ScanAsync(roots, _records, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested(); if (_closing) return;
            _store.Upsert(scanned); _records = _store.GetAll();
            Status.Text = "Поиск точных дубликатов по содержимому…";
            var records = _records;
            var duplicates = await Task.Run(() => _duplicateFinder.FindAsync(records, cancellationToken: cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested(); if (_closing) return;
            _store.Upsert(_records);
            _duplicateGroups = duplicates.Select((group, index) => new DuplicateGroupView($"Точные копии · группа {index + 1}", group)).ToList();
            _duplicatesChecked = true;
            var warnings = _scanner.WarningCount + _duplicateFinder.WarningCount;
            Status.Text = warnings == 0 ? $"Готово: {_records.Count:N0} файлов, групп точных дублей: {_duplicateGroups.Count:N0}"
                : $"Проверка неполная: недоступно или изменилось объектов: {warnings:N0}. Записи сохранены.";
        }
        catch (OperationCanceledException) { if (!_closing) Status.Text = "Остановлено пользователем. Исходные файлы не изменены."; }
        catch (Exception ex) { if (!_closing) { Status.Text = "Ошибка сканирования"; MessageBox.Show(ex.Message, "Фотоархив", MessageBoxButton.OK, MessageBoxImage.Warning); } }
        finally
        {
            _scanCancellation = null;
            if (!_closing)
            {
                ScanAction.Content = "Обновить"; AddFolderAction.IsEnabled = true;
                UpdateYearOptions(); UpdateSources(); UpdateStatistics(); RefreshGallery(); RefreshDuplicateCards(); UpdatePlaces(); UpdateFooter();
            }
        }
    }
    private void UpdateYearOptions()
    {
        _updatingYears = true; var previous = YearPicker.SelectedItem as string ?? "Все годы";
        var choices = new List<string> { "Все годы" };
        choices.AddRange(_records.Select(p => p.TakenAt.Year).Distinct().OrderByDescending(y => y).Select(y => y.ToString(CultureInfo.InvariantCulture)));
        YearPicker.ItemsSource = choices; YearPicker.SelectedItem = choices.Contains(previous) ? previous : "Все годы"; _updatingYears = false;
    }
    private void UpdateStatistics()
    {
        var stats = PhotoPresentation.Statistics(_records, _duplicatesChecked || _records.Count == 0 ? _duplicateGroups.Select(g => g.Photos) : null, _store.GetSources().Count);
        StatsCards.ItemsSource = new[] {
            new StatisticView("images", stats.Photos.ToString("N0"), "Фотографий в каталоге", "MetricPhotos"),
            new StatisticView("layers", stats.DuplicatePhotos?.ToString("N0") ?? "—", "Снимков в точных повторах", "MetricDuplicates"),
            new StatisticView("map", stats.Places.ToString("N0"), "Мест с координатами GPS", "MetricPlaces"),
            new StatisticView("harddrive", stats.Sources.ToString("N0"), "Источников в каталоге", "MetricSources") };
        DuplicateBadgeText.Text = _duplicatesChecked || _records.Count == 0 ? _duplicateGroups.Count.ToString("N0") : "—";
    }
    private void RefreshGallery()
    {
        if (!_initialized) return;
        _previews.Cancel(); _previews.Dispose(); _previews = new CancellationTokenSource();
        IEnumerable<PhotoRecord> filtered = _records;
        var keyword = SearchBox.Text.Trim(); if (keyword.Length > 0) filtered = filtered.Where(p => p.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (int.TryParse(YearPicker.SelectedItem as string, CultureInfo.InvariantCulture, out var year)) filtered = filtered.Where(p => p.TakenAt.Year == year);
        var selected = filtered.OrderByDescending(p => p.TakenAt).ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
        _filteredCount = selected.Count; _page = Math.Clamp(_page, 0, Math.Max(0, (selected.Count - 1) / PageSize));
        var page = selected.Skip(_page * PageSize).Take(PageSize).ToList();
        var totals = PhotoPresentation.GroupMonths(selected).ToDictionary(g => g.Month, g => g.Photos.Count);
        var duplicates = _duplicateGroups.SelectMany(g => g.Photos.Select(p => (p.Path, g.Photos.Count))).ToDictionary(p => p.Path, p => p.Count, StringComparer.OrdinalIgnoreCase);
        PhotosGallery.ItemsSource = PhotoPresentation.GroupMonths(page).Select(group => new MonthView(group.Title,
            group.Photos.Count == totals[group.Month] ? $"Фото: {group.Photos.Count:N0}" : $"Фото: {group.Photos.Count:N0} из {totals[group.Month]:N0}",
            group.Photos.Select(p => { var tile = new PhotoTile(p, duplicates.GetValueOrDefault(p.Path)); _ = tile.LoadAsync(_previews.Token); return tile; }).ToList())).ToList();
        PhotosEmpty.Visibility = selected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PhotosEmptyTitle.Text = _records.Count == 0 ? "Добавьте фотографии" : "Ничего не найдено";
        PhotosEmptyText.Text = _records.Count == 0 ? "Выберите папку — оригиналы останутся на своих местах." : "Попробуйте другое имя, путь или год.";
        UpdateFooter();
    }
    private void RefreshDuplicateCards()
    {
        _dupePreviews.Cancel(); _dupePreviews.Dispose(); _dupePreviews = new CancellationTokenSource();
        _dupePage = Math.Clamp(_dupePage, 0, Math.Max(0, (_duplicateGroups.Count - 1) / DuplicatePageSize));
        DuplicateGroups.ItemsSource = _duplicateGroups.Skip(_dupePage * DuplicatePageSize).Take(DuplicatePageSize).Select(g => g with {
            PreviewPhotos = g.Photos.Take(3).Select(photo => { var tile = new PhotoTile(photo); _ = tile.LoadAsync(_dupePreviews.Token); return tile; }).ToList()
        }).ToList();
        DuplicatesEmpty.Visibility = _duplicateGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DuplicatesEmptyTitle.Text = _duplicatesChecked ? "Точных повторов не найдено" : "Проверьте точные повторы";
        DuplicatesEmptyText.Text = _duplicatesChecked ? "Сравнивается только содержимое файлов. Ничего не удаляется." : "Нажмите «Обновить». Сравнение не изменяет фотографии.";
    }
    private void UpdatePlaces()
    {
        var places = _records.Where(PhotoPresentation.HasGps).OrderByDescending(p => p.TakenAt).Select(p => new LocationView(p)).ToList();
        PlacesList.ItemsSource = places; NoGpsText.Visibility = places.Count == 0 ? Visibility.Visible : Visibility.Collapsed; OpenMapAction.IsEnabled = false;
    }
    private void Places_Changed(object sender, SelectionChangedEventArgs e) { if (_initialized) OpenMapAction.IsEnabled = PlacesList.SelectedItem is LocationView; }
    private void GpsPreview_Loaded(object sender, RoutedEventArgs e) { if (!_closing && sender is FrameworkElement { DataContext: LocationView location }) _ = location.Tile.LoadAsync(_windowPreviews.Token); }
    private void UpdateFooter()
    {
        if (!_initialized) return;
        bool dupes = _currentPage == "duplicates";
        int page = dupes ? _dupePage : _page, count = dupes ? _duplicateGroups.Count : _filteredCount, size = dupes ? DuplicatePageSize : PageSize;
        PageCount.Text = $"{page + 1} / {Math.Max(1, (int)Math.Ceiling(count / (double)size))}";
        Summary.Text = dupes ? $"Групп в каталоге: {_duplicateGroups.Count:N0}" : $"{_records.Count:N0} в библиотеке · {_filteredCount:N0} по фильтру";
    }
    private void SetPage(string page)
    {
        _currentPage = page;
        PhotosPanel.Visibility = page == "photos" ? Visibility.Visible : Visibility.Collapsed;
        PlacesPanel.Visibility = page == "places" ? Visibility.Visible : Visibility.Collapsed;
        DuplicatesPanel.Visibility = page == "duplicates" ? Visibility.Visible : Visibility.Collapsed;
        AlbumsPanel.Visibility = page == "albums" ? Visibility.Visible : Visibility.Collapsed;
        SourcesPanel.Visibility = page == "sources" ? Visibility.Visible : Visibility.Collapsed;
        Pages.Visibility = page is "photos" or "duplicates" ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = page switch {
            "places" => ("Места съёмки", "Фотографии с координатами EXIF — только подлинные данные."),
            "duplicates" => ("Группы совпадений", "Сравнивайте точные копии — без удаления файлов."),
            "albums" => ("Альбомы", "Подборки по событиям без физических копий файлов."),
            "sources" => ("Источники", "Все места хранения фотографий — единый виртуальный каталог."),
            _ => ("Фотографии", "Вся коллекция в одной хронологии — независимо от папки и диска.") };
        foreach (var (button, name) in new[] { (NavPhotos,"photos"), (NavPlaces,"places"), (NavDuplicates,"duplicates"), (NavAlbums,"albums"), (NavSources,"sources") }) button.Tag = name == page ? "active" : null;
        PageScroll.ScrollToTop();
        if (page == "photos") RefreshGallery();
        if (page == "duplicates") { RefreshDuplicateCards(); SetDuplicateFilter(AllDuplicatesFilter); }
        UpdateFooter();
    }
    private void NavigatePhotos_Click(object sender, RoutedEventArgs e) => SetPage("photos");
    private void NavigatePlaces_Click(object sender, RoutedEventArgs e) => SetPage("places");
    private void NavigateDuplicates_Click(object sender, RoutedEventArgs e) => SetPage("duplicates");
    private void NavigateAlbums_Click(object sender, RoutedEventArgs e) => SetPage("albums");
    private void NavigateSources_Click(object sender, RoutedEventArgs e) => SetPage("sources");
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (!_initialized) return; _page = 0; if (_currentPage != "photos") SetPage("photos"); else RefreshGallery(); }
    private void Year_Changed(object sender, SelectionChangedEventArgs e) { if (!_initialized || _updatingYears) return; _page = 0; RefreshGallery(); PageScroll.ScrollToTop(); }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) { if (_currentPage == "duplicates") { _dupePage = Math.Max(0, _dupePage - 1); RefreshDuplicateCards(); } else { _page = Math.Max(0, _page - 1); RefreshGallery(); } UpdateFooter(); PageScroll.ScrollToTop(); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { if (_currentPage == "duplicates") { _dupePage++; RefreshDuplicateCards(); } else { _page++; RefreshGallery(); } UpdateFooter(); PageScroll.ScrollToTop(); }
    private void DuplicateFilter_Click(object sender, RoutedEventArgs e) { if (sender is Button button) SetDuplicateFilter(button); }
    private void SetDuplicateFilter(Button selected) { foreach (var button in new[] { AllDuplicatesFilter, ExactDuplicatesFilter }) { button.Background = (Brush)FindResource(button == selected ? "Soft" : "Canvas"); button.Foreground = (Brush)FindResource(button == selected ? "Accent" : "Muted"); } }
    private async void Photo_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: PhotoTile tile }) await ShowPhotoAsync(tile); }
    private async Task ShowPhotoAsync(PhotoTile tile)
    {
        _selectedPhoto = tile.Photo; var photo = tile.Photo;
        OpenOverlay(DetailsPanel); DetailsClose.Focus(); DetailName.Text = photo.Name; DetailPreview.Source = tile.Thumbnail;
        UpdatePhotoMetadata(photo, tile.Resolution, tile.PreviewFailed);
        await tile.LoadAsync(_windowPreviews.Token);
        if (_closing || _selectedPhoto != photo) return;
        DetailPreview.Source = tile.Thumbnail; UpdatePhotoMetadata(photo, tile.Resolution, tile.PreviewFailed);
    }
    private void UpdatePhotoMetadata(PhotoRecord photo, string resolution, bool previewFailed)
    {
        DetailMetadata.ItemsSource = new[] {
            new MetadataView("Дата", photo.TakenAt.ToString("dd MMMM yyyy HH:mm", PhotoPresentation.Russian)),
            new MetadataView("Источник даты", photo.DateSource), new MetadataView("GPS", PhotoPresentation.HasGps(photo) ? string.Format(CultureInfo.InvariantCulture,"{0:F6}, {1:F6}",photo.Latitude,photo.Longitude) : "Нет данных"),
            new MetadataView("Разрешение", resolution), new MetadataView("Размер", new PhotoTile(photo).SizeLabel), new MetadataView("Исходная папка", Path.GetDirectoryName(photo.Path) ?? "Не определено"), new MetadataView("Путь", photo.Path) };
        DetailAvailability.Text = photo.IsAvailable ? (previewFailed ? "Файл найден, но превью недоступно: формат не поддерживается или файл не прочитан." : "Файл доступен по исходному пути. Каталог не изменяет оригинал.") : "Источник недоступен. Запись сохранена; превью офлайн не кешируется.";
        DetailMapAction.Visibility = DetailMapWarning.Visibility = PhotoPresentation.HasGps(photo) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CompareGroup_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: DuplicateGroupView group }) { DuplicateMembers.ItemsSource = group.Photos; OpenOverlay(ComparisonPanel); ComparisonClose.Focus(); } }
    private async void DuplicateMember_Click(object sender, MouseButtonEventArgs e) { if (DuplicateMembers.SelectedItem is PhotoRecord photo) await ShowPhotoAsync(new PhotoTile(photo)); }
    private void OpenOverlay(Border panel)
    {
        if (OverlayLayer.Visibility != Visibility.Visible) _returnFocus = Keyboard.FocusedElement;
        DetailsPanel.Visibility = ArchivePanel.Visibility = ComparisonPanel.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible; OverlayLayer.Visibility = Visibility.Visible; MainShell.IsEnabled = false;
        MainShell.Effect = panel == DetailsPanel ? null : new BlurEffect { Radius = 3 };
        OverlayShade.Background = new SolidColorBrush(panel == DetailsPanel ? Color.FromArgb(51,17,30,48) : Color.FromArgb(82,16,29,49));
    }
    private void CloseOverlay()
    {
        DetailsPanel.Visibility = ArchivePanel.Visibility = ComparisonPanel.Visibility = OverlayLayer.Visibility = Visibility.Collapsed;
        _selectedPhoto = null; MainShell.Effect = null; MainShell.IsEnabled = true; _returnFocus?.Focus();
    }
    private void CloseOverlay_Click(object sender, RoutedEventArgs e) => CloseOverlay();
    private void OverlayShade_Click(object sender, MouseButtonEventArgs e) => CloseOverlay();
    private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape && OverlayLayer.Visibility == Visibility.Visible) { CloseOverlay(); e.Handled = true; } }
    private void ExportPlaceholder_Click(object sender, RoutedEventArgs e) { OpenOverlay(ArchivePanel); ArchiveClose.Focus(); }
    private void OpenMap_Click(object sender, RoutedEventArgs e) { if (PlacesList.SelectedItem is LocationView location) OpenMap(location.Photo); }
    private void DetailMap_Click(object sender, RoutedEventArgs e) { if (_selectedPhoto is { } photo) OpenMap(photo); }
    private void OpenMap(PhotoRecord photo)
    {
        if (!PhotoPresentation.HasGps(photo)) return;
        var lat = photo.Latitude!.Value.ToString("F6",CultureInfo.InvariantCulture); var lon = photo.Longitude!.Value.ToString("F6",CultureInfo.InvariantCulture);
        try { Process.Start(new ProcessStartInfo($"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=15/{lat}/{lon}") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message,"Не удалось открыть карту"); }
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { if (_initialized) ApplyResponsiveLayout(); }
    private void ApplyResponsiveLayout()
    {
        double width = ActualWidth; bool compact = width <= 900;
        SidebarColumn.Width = new GridLength(compact ? 65 : width <= 1230 ? 205 : 232);
        SidebarLayout.Margin = compact ? new Thickness(9,25,9,20) : new Thickness(15,29,15,20);
        Brand.Margin = compact ? new Thickness(8,0,0,44) : new Thickness(12,0,0,44);
        foreach (var element in new FrameworkElement[] { BrandName, NavigationHeading, SidebarSourcesPanel, SidebarBottom, NavPhotosLabel, NavPlacesLabel, NavDuplicatesLabel, NavAlbumsLabel, NavSourcesLabel, DuplicateBadge }) element.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PhotoColumns = width > 1230 ? 5 : compact ? 3 : 4; StatsColumns = width > 1230 ? 4 : 2; DupeColumns = compact ? 1 : 2;
        int pad = width <= 1230 ? 24 : 35; PageContent.Margin = new Thickness(pad, width <= 1230 ? 28 : 34, pad,35);
        TopbarLayout.Margin = new Thickness(width <= 1230 ? 23 : 33,0,width <= 1230 ? 23 : 33,0);
        AddFolderLabel.Visibility = ArchiveLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AddFolderAction.Padding = ArchiveAction.Padding = new Thickness(compact ? 12 : 16,0,compact ? 12 : 16,0);
        SearchFrame.Width = Math.Max(120,Math.Min(compact ? 360 : 500,width-SidebarColumn.Width.Value-(width<=1230?46:66)-(compact?110:340)-19));
        SafetyBadge.Visibility = width < 1000 ? Visibility.Collapsed : Visibility.Visible; SortLabel.Visibility = width < 1000 ? Visibility.Collapsed : Visibility.Visible;
        bool stackMap = compact; MapLeftColumn.Width = new GridLength(stackMap ? 1 : 2.25,GridUnitType.Star); MapGapColumn.Width = new GridLength(stackMap ? 0 : 20); MapRightColumn.Width = new GridLength(stackMap ? 0 : 1,GridUnitType.Star); MapRightColumn.MinWidth = stackMap ? 0 : 280;
        Grid.SetColumn(GpsCard,stackMap?0:2); Grid.SetRow(GpsCard,stackMap?1:0); GpsCard.Margin = new Thickness(0,stackMap?20:0,0,0);
        DetailsPanel.Width = Math.Min(420,Math.Max(300,width*0.9)); DetailPreviewFrame.Height = (DetailsPanel.Width-54)*0.75;
        ArchivePanel.Width = ComparisonPanel.Width = Math.Max(300,Math.Min(720,width-40)); ArchivePanel.MaxHeight = ComparisonPanel.MaxHeight = Math.Max(220,ActualHeight-80);
    }
    protected override void OnClosed(EventArgs e)
    {
        _closing = true; _scanCancellation?.Cancel(); _previews.Cancel(); _previews.Dispose(); _dupePreviews.Cancel(); _dupePreviews.Dispose(); _windowPreviews.Cancel(); _windowPreviews.Dispose(); _store.Dispose(); base.OnClosed(e);
    }
}
