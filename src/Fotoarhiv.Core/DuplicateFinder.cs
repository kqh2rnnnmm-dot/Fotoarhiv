using System.Security.Cryptography;

namespace Fotoarhiv.Core;

/// <summary>Exact content equality only, never visual similarity. No writes to photos.</summary>
public sealed class DuplicateFinder
{
    public int WarningCount { get; private set; }

    public async Task<List<List<PhotoRecord>>> FindAsync(IEnumerable<PhotoRecord> records,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        WarningCount = 0;
        cancellationToken.ThrowIfCancellationRequested();
        var groups = new List<List<PhotoRecord>>();
        int completed = 0;
        foreach (var sizeGroup in records.Where(p => p.IsAvailable).GroupBy(p => p.Size).Where(g => g.Count() > 1))
        {
            var hashes = new Dictionary<string, List<PhotoRecord>>(StringComparer.Ordinal);
            foreach (var record in sizeGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Validate even cached hashes; a rescan is not required before this call.
                    var before = new FileInfo(record.Path);
                    if (!before.Exists || before.Length != record.Size || before.LastWriteTimeUtc.Ticks != record.ModifiedUtcTicks)
                    {
                        record.Sha256 = null;
                        WarningCount++;
                        continue;
                    }
                    var hash = record.Sha256;
                    if (string.IsNullOrEmpty(hash))
                    {
                        // Read-only, and avoid caching a hash if the photo changed mid-read.
                        await using var stream = new FileStream(record.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                            bufferSize: 1024 * 128, options: FileOptions.SequentialScan | FileOptions.Asynchronous);
                        using var sha = SHA256.Create();
                        hash = Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken));
                        before.Refresh();
                        if (!before.Exists || before.Length != record.Size || before.LastWriteTimeUtc.Ticks != record.ModifiedUtcTicks)
                        { record.Sha256 = null; WarningCount++; continue; }
                        record.Sha256 = hash;
                    }
                    if (!hashes.TryGetValue(hash, out var bucket)) hashes[hash] = bucket = [];
                    bucket.Add(record);
                }
                catch (IOException) { record.Sha256 = null; WarningCount++; }
                catch (UnauthorizedAccessException) { record.Sha256 = null; WarningCount++; }
                completed++;
                if (completed % 20 == 0) progress?.Report(completed);
            }
            groups.AddRange(hashes.Values.Where(b => b.Count > 1));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return groups.OrderByDescending(g => g.Count).ToList();
    }
}
