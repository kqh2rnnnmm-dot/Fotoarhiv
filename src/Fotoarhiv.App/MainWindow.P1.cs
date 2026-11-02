using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fotoarhiv.Core;

namespace Fotoarhiv.App;

public partial class MainWindow
{
    private LibraryViewState _view = null!;
    private ThumbnailService _thumbnailService = null!;
    private SortPreferenceStore _sortStore = null!;
    private Task _settingsWrite = Task.CompletedTask;
    private List<SourceView> _sourceViews = [];
    private List<PhotoTile> _galleryTiles = [];
    private IReadOnlyList<PhotoRecord> _orderedPhotos = [];
    private readonly List<FrameworkElement> _galleryElements = [], _duplicateElements = [];
    private CancellationTokenSource _sourceProbes = new(), _placePreviews = new();
    private CancellationTokenSource? _viewerLoad;
    private ViewerSequence? _viewerSequence;
    private OriginalBitmap? _viewerOriginal;
    private PhotoTile? _detailTile;
    private int _viewerRequest;
    private bool _previewScheduled, _actualSize, _closeApproved, _closeStarted, _panning;
    private Point _panStart;
    private double _panHorizontal, _panVertical;

    private void InitializeP1(string applicationFolder)
    {
        _thumbnailService = new ThumbnailService(Path.Combine(applicationFolder, "Cache", "Thumbnails"));
        _sortStore = new SortPreferenceStore(applicationFolder);
        _view = new LibraryViewState(_records, _store.GetSources(), _sortStore.Load());
        Closing += ClosingP1; RootGrid.SizeChanged += (_, _) => ApplyP1Layout();
        UpdateSortLabel();
    }
    private PhotoTile Tile(PhotoRecord photo, int duplicates = 0) => new(photo, duplicates, _thumbnailService) { IsSelected = _view.Selection.Contains(photo.Path) };
    private List<DuplicateGroupView> VisibleGroups() => _view.VisibleDuplicates(_duplicateGroups.Select(g => (IReadOnlyList<PhotoRecord>)g.Photos)).Select((photos, i) => new DuplicateGroupView($"Точные копии · группа {i + 1}", photos)).ToList();
    private void RefreshFilteredView(bool years = false)
    {
        _page = _dupePage = 0;
        if (years) UpdateYearOptions();
        SourceVisibilitySummary.Text = $"Показано источников: {_view.VisibleSourceCount} из {_view.Sources.Count}";
        UpdateStatistics(); RefreshGallery(); RefreshDuplicateCards(); UpdatePlaces(); UpdateSelectionUI();
        PageScroll.ScrollToTop(); SchedulePreviews();
    }
    private async void Sort_Click(object sender, RoutedEventArgs e)
    {
        _view.SetSort(_view.Sort == SortDirection.NewestFirst ? SortDirection.OldestFirst : SortDirection.NewestFirst);
        UpdateSortLabel(); RefreshFilteredView();
        _settingsWrite = _sortStore.SaveAsync(_view.Sort);
        try { await _settingsWrite; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (!_closing) Status.Text = "Порядок изменён, но настройка не сохранена."; }
    }
    private void UpdateSortLabel()
    {
        SortText.Text = _view.Sort == SortDirection.OldestFirst ? "Сначала старые" : "Сначала новые";
        System.Windows.Automation.AutomationProperties.SetName(SortAction, SortText.Text);
    }
    private void SourceVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not CheckBox { DataContext: SourceView source } checkbox || source.IsIncluded == (checkbox.IsChecked == true)) return;
        _view.SetSourceVisible(source.FullPath, checkbox.IsChecked == true); source.IsIncluded = _view.IsSourceVisible(source.FullPath);
        RefreshFilteredView(years: true);
    }
    private void ShowAllSources_Click(object sender, RoutedEventArgs e)
    {
        _view.ShowAllSources(); foreach (var source in _sourceViews) source.IsIncluded = true; RefreshFilteredView(years: true);
    }
    private void HideAllSources_Click(object sender, RoutedEventArgs e)
    {
        _view.HideAllSources(); foreach (var source in _sourceViews) source.IsIncluded = false; RefreshFilteredView(years: true);
    }
    private void EmptyPhotos_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Sources.Count > 0 && _view.VisibleSourceCount == 0) SetPage("sources"); else AddFolder_Click(sender, e);
    }
    private void RefreshSourceAvailability()
    {
        foreach (var source in _sourceViews) _ = source.RefreshAvailabilityAsync(_sourceProbes.Token);
    }
    private void Selection_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not CheckBox { DataContext: PhotoTile tile } checkbox || tile.IsSelected == (checkbox.IsChecked == true)) return;
        _view.Selection.Toggle(tile.Photo.Path); UpdateSelectionUI();
        e.Handled = true;
    }
    private void SelectTile(PhotoTile tile)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _view.Selection.SelectRange(tile.Photo.Path, _orderedPhotos, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        else _view.Selection.Toggle(tile.Photo.Path);
        UpdateSelectionUI();
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { _view.Selection.Clear(); UpdateSelectionUI(); }
    private void UpdateSelectionUI()
    {
        foreach (var tile in _galleryTiles) tile.IsSelected = _view.Selection.Contains(tile.Photo.Path);
        SelectionCount.Text = $"Выбрано: {_view.Selection.Count:N0}";
        ClearSelectionAction.IsEnabled = _view.Selection.Count > 0;
        SelectionBar.Visibility = _view.Selection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PhotoPreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var list = Equals(element.Tag, "duplicate") ? _duplicateElements : _galleryElements;
        if (!list.Contains(element)) list.Add(element); SchedulePreviews();
    }
    private void PageScroll_Changed(object sender, ScrollChangedEventArgs e) => SchedulePreviews();
    private void SchedulePreviews()
    {
        if (!_initialized || _closing || _previewScheduled) return;
        _previewScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _previewScheduled = false; if (_closing || PageScroll.ActualHeight <= 0) return;
            var elements = _currentPage == "duplicates" ? _duplicateElements : _galleryElements;
            if (_currentPage is not "photos" and not "duplicates") return;
            var viewport = new Rect(0, -220, PageScroll.ActualWidth, PageScroll.ActualHeight + 440);
            var token = _currentPage == "duplicates" ? _dupePreviews.Token : _previews.Token;
            foreach (var element in elements.ToArray())
            {
                if (!element.IsVisible || element.DataContext is not PhotoTile tile || tile.Thumbnail is not null || tile.PreviewFailed) continue;
                try
                {
                    var bounds = new Rect(element.TranslatePoint(new Point(), PageScroll), element.RenderSize);
                    if (bounds.IntersectsWith(viewport)) _ = tile.LoadAsync(token);
                }
                catch (InvalidOperationException) { /* An old page may have left the visual tree. */ }
            }
        }));
    }
    private void OpenViewer_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPhoto is not { } photo) return;
        var sequence = _orderedPhotos.Any(p => PathIdentity.Normalize(p.Path) == PathIdentity.Normalize(photo.Path)) ? _orderedPhotos : new[] { photo };
        _viewerSequence = new ViewerSequence(sequence, photo.Path);
        ApplyP1Layout(); OpenOverlay(ViewerPanel); ViewerClose.Focus(); _ = LoadViewerPhotoAsync();
    }
    private async Task LoadViewerPhotoAsync()
    {
        if (_viewerSequence is null) return;
        _viewerLoad?.Cancel(); _viewerLoad?.Dispose(); _viewerLoad = new CancellationTokenSource();
        var token = _viewerLoad.Token; int request = ++_viewerRequest;
        var photo = _viewerSequence.Current; _selectedPhoto = photo; _viewerOriginal = null; _actualSize = false;
        ViewerTitle.Text = photo.Name; ViewerTitle.ToolTip = photo.Name;
        ViewerMetadata.Text = $"{PhotoPresentation.DateCaption(photo)} · {new PhotoTile(photo).SizeLabel} · {Path.GetDirectoryName(photo.Path)}";
        ViewerMetadata.ToolTip = ViewerMetadata.Text;
        ViewerPosition.Text = $"{_viewerSequence.Index + 1} / {_viewerSequence.Photos.Count}";
        ViewerPreviousAction.IsEnabled = _viewerSequence.HasPrevious; ViewerNextAction.IsEnabled = _viewerSequence.HasNext;
        ViewerActualAction.IsEnabled = false; ViewerMessage.Text = "Загрузка оригинала…";
        ViewerImage.Source = _detailTile?.Photo.Path == photo.Path ? _detailTile.Thumbnail : null;
        ApplyViewerScale();
        var thumbnail = LoadViewerThumbnailAsync(photo, request, token);
        try
        {
            var original = await _thumbnailService.LoadOriginalAsync(photo, token); token.ThrowIfCancellationRequested();
            if (_closing || request != _viewerRequest) return;
            _viewerOriginal = original;
            if (original.Bitmap is not null) ViewerImage.Source = original.Bitmap;
            ViewerActualAction.IsEnabled = original.FullResolution;
            ViewerMessage.Text = original.Message;
            if (original.Bitmap is null && original.Available && ViewerImage.Source is not null) ViewerMessage.Text = "Только миниатюра. " + original.Message;
            if (original.Width > 0) ViewerMetadata.Text = $"{original.Width:N0} × {original.Height:N0} · {ViewerMetadata.Text}";
            ApplyViewerScale(); await thumbnail;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task LoadViewerThumbnailAsync(PhotoRecord photo, int request, CancellationToken token)
    {
        try
        {
            var preview = await _thumbnailService.GetAsync(photo, token); token.ThrowIfCancellationRequested();
            if (_closing || request != _viewerRequest || _viewerOriginal?.Bitmap is not null) return;
            ViewerImage.Source = preview.Bitmap;
            if (_viewerOriginal is { Available: false }) ViewerMessage.Text = preview.Bitmap is null ? "Источник недоступен. Миниатюры нет в кеше." : "Только миниатюра, источник недоступен";
            else if (_viewerOriginal is { Available: true }) ViewerMessage.Text = (preview.Bitmap is null ? "" : "Только миниатюра. ") + _viewerOriginal.Message;
            ApplyViewerScale();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }
    private void ViewerPrevious_Click(object sender, RoutedEventArgs e) { if (_viewerSequence?.Move(-1) == true) _ = LoadViewerPhotoAsync(); }
    private void ViewerNext_Click(object sender, RoutedEventArgs e) { if (_viewerSequence?.Move(1) == true) _ = LoadViewerPhotoAsync(); }
    private void ViewerFit_Click(object sender, RoutedEventArgs e) { _actualSize = false; ApplyViewerScale(); }
    private void ViewerActual_Click(object sender, RoutedEventArgs e) { if (_viewerOriginal?.FullResolution == true) { _actualSize = true; ApplyViewerScale(); } }
    private void ViewerViewport_SizeChanged(object sender, SizeChangedEventArgs e) { if (!_actualSize) ApplyViewerScale(); }
    private void ApplyViewerScale()
    {
        if (ViewerImage.Source is not BitmapSource bitmap) { ViewerEmpty.Visibility = Visibility.Visible; return; }
        ViewerEmpty.Visibility = Visibility.Collapsed;
        var dpi = VisualTreeHelper.GetDpi(ViewerPanel);
        double scale = _actualSize ? 1 : Math.Min(1, Math.Min(Math.Max(1, ViewerScroll.ActualWidth - 24) * dpi.DpiScaleX / bitmap.PixelWidth, Math.Max(1, ViewerScroll.ActualHeight - 24) * dpi.DpiScaleY / bitmap.PixelHeight));
        double width = bitmap.PixelWidth * scale / dpi.DpiScaleX, height = bitmap.PixelHeight * scale / dpi.DpiScaleY;
        if (double.IsNaN(ViewerImage.Width) || Math.Abs(ViewerImage.Width - width) > 0.1) ViewerImage.Width = width;
        if (double.IsNaN(ViewerImage.Height) || Math.Abs(ViewerImage.Height - height) > 0.1) ViewerImage.Height = height;
        ViewerFitAction.Background = (Brush)FindResource(_actualSize ? "Canvas" : "Soft");
        ViewerActualAction.Background = (Brush)FindResource(_actualSize ? "Soft" : "Canvas");
        ViewerZoom.Text = _actualSize ? "100%" : "Вписать";
        if (!_actualSize) { ViewerScroll.ScrollToHorizontalOffset(0); ViewerScroll.ScrollToVerticalOffset(0); }
    }
    private void ViewerPan_Down(object sender, MouseButtonEventArgs e)
    {
        if (!_actualSize || ViewerImage.Source is null) return;
        _panning = true; _panStart = e.GetPosition(ViewerScroll); _panHorizontal = ViewerScroll.HorizontalOffset; _panVertical = ViewerScroll.VerticalOffset;
        ViewerScroll.CaptureMouse(); ViewerScroll.Cursor = Cursors.SizeAll; e.Handled = true;
    }
    private void ViewerPan_Move(object sender, MouseEventArgs e)
    {
        if (!_panning || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(ViewerScroll); ViewerScroll.ScrollToHorizontalOffset(_panHorizontal + _panStart.X - point.X); ViewerScroll.ScrollToVerticalOffset(_panVertical + _panStart.Y - point.Y);
    }
    private void ViewerPan_Up(object sender, MouseButtonEventArgs e) { _panning = false; ViewerScroll.ReleaseMouseCapture(); ViewerScroll.Cursor = Cursors.Arrow; }
    private void CloseViewerResources()
    {
        _viewerLoad?.Cancel(); _viewerRequest++; _viewerOriginal = null; _viewerSequence = null; ViewerImage.Source = null; _panning = false; ViewerScroll.ReleaseMouseCapture(); ViewerScroll.Cursor = Cursors.Arrow;
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi); if (_initialized) Dispatcher.BeginInvoke(new Action(ApplyViewerScale));
    }
    private void ApplyP1Layout()
    {
        ViewerPanel.Width = Math.Max(300, RootGrid.ActualWidth - 40); ViewerPanel.Height = Math.Max(240, RootGrid.ActualHeight - 40);
        SchedulePreviews();
    }
    private async void ClosingP1(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true; if (_closeStarted) return; _closeStarted = true; _closing = true;
        _scanCancellation?.Cancel(); _previews.Cancel(); _dupePreviews.Cancel(); _windowPreviews.Cancel(); _sourceProbes.Cancel(); _placePreviews.Cancel(); _viewerLoad?.Cancel();
        RootGrid.IsEnabled = false; await Task.Yield();
        try { await _settingsWrite; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var cache = _thumbnailService.DisposeAsync().AsTask(); await Task.WhenAny(cache, Task.Delay(2000));
        _closeApproved = true; Close();
    }
}
