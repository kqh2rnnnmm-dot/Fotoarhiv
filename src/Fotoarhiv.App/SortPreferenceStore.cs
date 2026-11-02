using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Fotoarhiv.App;

public sealed class SortPreferenceStore(string applicationFolder)
{
    private readonly string _path = Path.Combine(applicationFolder, "settings.json");
    private readonly object _gate = new();
    private Task _pending = Task.CompletedTask;
    public SortDirection Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 4096) return SortDirection.NewestFirst;
            using var json = JsonDocument.Parse(File.ReadAllText(_path));
            return json.RootElement.TryGetProperty("sort", out var sort) && sort.GetString() == "OldestFirst" ? SortDirection.OldestFirst : SortDirection.NewestFirst;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return SortDirection.NewestFirst; }
    }
    public Task SaveAsync(SortDirection sort)
    {
        lock (_gate) return _pending = SaveAfterAsync(_pending, sort);
    }
    private async Task SaveAfterAsync(Task previous, SortDirection sort)
    {
        try { await previous.ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { version = 1, sort = sort.ToString() }));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }
}
