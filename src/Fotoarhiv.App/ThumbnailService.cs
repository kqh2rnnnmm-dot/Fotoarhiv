using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Fotoarhiv.Core;

namespace Fotoarhiv.App;

public sealed record PreviewBitmap(BitmapSource? Bitmap, ThumbnailResult State);
public sealed record OriginalBitmap(BitmapSource? Bitmap, int Width, int Height, bool FullResolution, bool Available, string Message);

/// <summary>WPF codec adapter; all decoding and cache IO run away from the dispatcher.</summary>
public sealed class ThumbnailService : IAsyncDisposable
{
    private readonly ThumbnailCache _cache;
    private readonly SemaphoreSlim _cachedDecoders = new(4);
    private readonly SemaphoreSlim _originalDecoder = new(1);
    public const long OriginalPixelLimit = 32_000_000;
    public ThumbnailService(string cacheFolder) => _cache = new ThumbnailCache(cacheFolder, DecodeThumbnail, validate: ValidateThumbnail);

    public async Task<PreviewBitmap> GetAsync(PhotoRecord photo, CancellationToken token)
    {
        var state = await _cache.GetAsync(photo, token).ConfigureAwait(false);
        if (state.Image is null) return new(null, state);
        await _cachedDecoders.WaitAsync(token).ConfigureAwait(false);
        try { return new(await Task.Run(() => DecodeMemory(state.Image.EncodedImage), token).ConfigureAwait(false), state); }
        catch (Exception ex) when (CodecFailure(ex)) { return new(null, state with { Image = null, Reason = "Миниатюра не прочитана" }); }
        finally { _cachedDecoders.Release(); }
    }
    private static ValueTask<ThumbnailPixels> DecodeThumbnail(string path, CancellationToken token)
    {
        try
        {
            var decoded = DecodeFile(path, 512, 512L * 512, token);
            var encoder = new PngBitmapEncoder();
            // Deliberately omit original EXIF, GPS, thumbnails and color-context metadata.
            encoder.Frames.Add(BitmapFrame.Create(decoded.Bitmap!, null, null, null));
            using var output = new MemoryStream(); encoder.Save(output); token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ThumbnailPixels(output.ToArray(), decoded.Bitmap!.PixelWidth, decoded.Bitmap.PixelHeight, decoded.Width, decoded.Height));
        }
        catch (Exception ex) when (CodecFailure(ex)) { throw new IOException("Image cannot be decoded", ex); }
    }
    private static bool ValidateThumbnail(ThumbnailPixels pixels)
    {
        try { var image = DecodeMemory(pixels.EncodedImage); return image.PixelWidth == pixels.Width && image.PixelHeight == pixels.Height; }
        catch (Exception ex) when (CodecFailure(ex)) { return false; }
    }
    private static BitmapSource DecodeMemory(byte[] payload)
    {
        using var input = new MemoryStream(payload, writable: false);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.StreamSource = input;
        bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    private static bool CodecFailure(Exception ex) => ex is IOException or System.IO.FileFormatException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or OutOfMemoryException;

    public async Task<OriginalBitmap> LoadOriginalAsync(PhotoRecord photo, CancellationToken token)
    {
        await _originalDecoder.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var before = new FileInfo(photo.Path); before.Refresh();
                    if (!before.Exists) return new OriginalBitmap(null, 0, 0, false, false, "Только миниатюра, источник недоступен");
                    long size = before.Length, time = before.LastWriteTimeUtc.Ticks;
                    var result = DecodeFile(photo.Path, 8192, OriginalPixelLimit, token);
                    var after = new FileInfo(photo.Path); after.Refresh();
                    if (!after.Exists || after.Length != size || after.LastWriteTimeUtc.Ticks != time)
                        return new OriginalBitmap(null, 0, 0, false, after.Exists, "Файл изменился во время чтения. Повторите открытие.");
                    return result;
                }
                catch (Exception ex) when (CodecFailure(ex))
                {
                    bool available = File.Exists(photo.Path);
                    return new OriginalBitmap(null, 0, 0, false, available, available ? "Оригинал не прочитан: формат не поддерживается или доступ ограничен." : "Только миниатюра, источник недоступен");
                }
            }, token).ConfigureAwait(false);
        }
        finally { _originalDecoder.Release(); }
    }

    private static OriginalBitmap DecodeFile(string path, int maxEdge, long maxPixels, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); int width, height;
        using (var header = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var decoder = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) throw new IOException("No image frame");
            width = decoder.Frames[0].PixelWidth; height = decoder.Frames[0].PixelHeight;
        }
        if (width <= 0 || height <= 0) throw new IOException("Invalid image size");
        double scale = Math.Min(1, Math.Min(maxEdge / (double)Math.Max(width, height), Math.Sqrt(maxPixels / ((double)width * height))));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.StreamSource = input; bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (scale < 1) { bitmap.DecodePixelWidth = Math.Max(1, (int)(width * scale)); bitmap.DecodePixelHeight = Math.Max(1, (int)(height * scale)); }
        bitmap.EndInit(); bitmap.Freeze(); token.ThrowIfCancellationRequested();
        bool full = bitmap.PixelWidth == width && bitmap.PixelHeight == height;
        return new(bitmap, width, height, full, true, full ? "Оригинал" : "Просмотр ограничен 32 Мп и 8192 пикселями по стороне. Режим 100% недоступен.");
    }
    public ValueTask DisposeAsync() => _cache.DisposeAsync();
}
