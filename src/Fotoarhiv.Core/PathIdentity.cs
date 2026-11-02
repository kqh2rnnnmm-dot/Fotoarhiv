namespace Fotoarhiv.Core;

/// <summary>Logical Windows path identity; never enumerates or resolves source directories.</summary>
public static class PathIdentity
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = path.Replace('/', '\\');
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) value = @"\\" + value[8..];
        else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        bool drive = value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && value[2] == '\\';
        bool unc = value.StartsWith(@"\\", StringComparison.Ordinal);
        if (!drive && !unc) value = Path.GetFullPath(path).Replace('/', '\\');
        var prefix = value.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\" : "";
        var pieces = value.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>();
        int protectedPieces = prefix.Length > 0 ? 2 : pieces.Length > 0 && pieces[0].EndsWith(':') ? 1 : 0;
        foreach (var piece in pieces)
        {
            if (piece == ".") continue;
            if (piece == "..") { if (clean.Count > protectedPieces) clean.RemoveAt(clean.Count - 1); }
            else clean.Add(piece);
        }
        return (prefix + string.Join('\\', clean)).ToUpperInvariant();
    }

    public static bool BelongsTo(string file, string root) => Normalize(file).StartsWith(Normalize(root).TrimEnd('\\') + "\\", StringComparison.Ordinal);
}
