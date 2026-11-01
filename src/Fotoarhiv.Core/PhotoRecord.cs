namespace Fotoarhiv.Core;

public sealed class PhotoRecord
{
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public long Size { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public DateTime TakenAt { get; set; }
    public string DateSource { get; set; } = "Дата файла";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Sha256 { get; set; }
    public bool IsAvailable => File.Exists(Path);
}

public sealed record ScanProgress(int Scanned, string CurrentPath);
