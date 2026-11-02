using Microsoft.Data.Sqlite;

namespace Fotoarhiv.Core;

/// <summary>SQLite catalog; does not rewrite or delete the user's picture files.</summary>
public sealed class CatalogStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public CatalogStore(string databasePath)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(databasePath))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // One long-lived connection; release native file handles when the store closes.
            Pooling = false
        }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sources(path TEXT PRIMARY KEY COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS photos(
              path TEXT PRIMARY KEY COLLATE NOCASE,
              bytes INTEGER NOT NULL,
              modified_utc_ticks INTEGER NOT NULL,
              capture_ticks INTEGER NOT NULL,
              date_source TEXT NOT NULL,
              latitude REAL NULL,
              longitude REAL NULL,
              sha256 TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_capture ON photos(capture_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_bytes ON photos(bytes);
            """;
        command.ExecuteNonQuery();
    }

    public void AddSource(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO sources(path) VALUES($path)";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.ExecuteNonQuery();
    }

    public List<string> GetSources()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path FROM sources ORDER BY path";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public List<PhotoRecord> GetAll()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path,bytes,modified_utc_ticks,capture_ticks,date_source,latitude,longitude,sha256 FROM photos ORDER BY capture_ticks DESC";
        using var reader = command.ExecuteReader();
        var result = new List<PhotoRecord>();
        while (reader.Read())
        {
            result.Add(new PhotoRecord
            {
                Path = reader.GetString(0),
                Size = reader.GetInt64(1),
                ModifiedUtcTicks = reader.GetInt64(2),
                TakenAt = new DateTime(reader.GetInt64(3)),
                DateSource = reader.GetString(4),
                Latitude = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                Longitude = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                Sha256 = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }
        return result;
    }

    public void Upsert(IEnumerable<PhotoRecord> records)
    {
        using var tx = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO photos(path,bytes,modified_utc_ticks,capture_ticks,date_source,latitude,longitude,sha256)
            VALUES($path,$bytes,$modified,$captured,$source,$lat,$lon,$hash)
            ON CONFLICT(path) DO UPDATE SET
              bytes=excluded.bytes,
              modified_utc_ticks=excluded.modified_utc_ticks,
              capture_ticks=excluded.capture_ticks,
              date_source=excluded.date_source,
              latitude=excluded.latitude,
              longitude=excluded.longitude,
              sha256=excluded.sha256
            """;
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var bytes = command.Parameters.Add("$bytes", SqliteType.Integer);
        var modified = command.Parameters.Add("$modified", SqliteType.Integer);
        var captured = command.Parameters.Add("$captured", SqliteType.Integer);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var lat = command.Parameters.Add("$lat", SqliteType.Real);
        var lon = command.Parameters.Add("$lon", SqliteType.Real);
        var hash = command.Parameters.Add("$hash", SqliteType.Text);
        foreach (var item in records)
        {
            path.Value = item.Path;
            bytes.Value = item.Size;
            modified.Value = item.ModifiedUtcTicks;
            captured.Value = item.TakenAt.Ticks;
            source.Value = item.DateSource;
            lat.Value = item.Latitude.HasValue ? item.Latitude.Value : DBNull.Value;
            lon.Value = item.Longitude.HasValue ? item.Longitude.Value : DBNull.Value;
            hash.Value = item.Sha256 is null ? DBNull.Value : item.Sha256;
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Dispose() => _connection.Dispose();
}
