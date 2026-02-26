using Microsoft.Data.Sqlite;

namespace Orpheus.Core.Library;

/// <summary>
/// SQLite-backed implementation of IMediaLibrary.
/// Thread-safe — uses connection pooling via Microsoft.Data.Sqlite.
/// </summary>
public sealed class SqliteMediaLibrary : IMediaLibrary
{
    private readonly string _connectionString;
    private bool _disposed;

    public SqliteMediaLibrary(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var conn = OpenConnection();

        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tracks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL UNIQUE,
                file_size INTEGER NOT NULL DEFAULT 0,
                last_modified_ticks INTEGER NOT NULL DEFAULT 0,
                date_added_ticks INTEGER NOT NULL DEFAULT 0,
                title TEXT,
                artist TEXT,
                album TEXT,
                album_artist TEXT,
                track_number INTEGER,
                track_count INTEGER,
                disc_number INTEGER,
                year INTEGER,
                genre TEXT,
                duration_ms INTEGER,
                bitrate INTEGER,
                sample_rate INTEGER,
                channels INTEGER,
                codec TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_tracks_artist ON tracks(artist);
            CREATE INDEX IF NOT EXISTS idx_tracks_album ON tracks(album);
            CREATE INDEX IF NOT EXISTS idx_tracks_genre ON tracks(genre);
            CREATE INDEX IF NOT EXISTS idx_tracks_title ON tracks(title);

            CREATE TABLE IF NOT EXISTS watched_folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                folder_path TEXT NOT NULL UNIQUE
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection() => new(_connectionString);

    public async Task<int> GetTrackCountAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tracks";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<LibraryTrack>> GetAllTracksAsync(
        TrackSortOrder sortOrder = TrackSortOrder.Title,
        CancellationToken cancellationToken = default)
    {
        var orderBy = sortOrder switch
        {
            TrackSortOrder.Title => "title COLLATE NOCASE, artist COLLATE NOCASE",
            TrackSortOrder.Artist => "artist COLLATE NOCASE, album COLLATE NOCASE, track_number",
            TrackSortOrder.Album => "album COLLATE NOCASE, disc_number, track_number",
            TrackSortOrder.Genre => "genre COLLATE NOCASE, artist COLLATE NOCASE, title COLLATE NOCASE",
            TrackSortOrder.Year => "year DESC, album COLLATE NOCASE, track_number",
            TrackSortOrder.Duration => "duration_ms",
            TrackSortOrder.DateAdded => "date_added_ticks DESC",
            TrackSortOrder.FilePath => "file_path COLLATE NOCASE",
            _ => "title COLLATE NOCASE"
        };

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM tracks ORDER BY {orderBy}";
        return await ReadTracksAsync(cmd, cancellationToken);
    }

    public async Task<LibraryTrack?> GetTrackByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM tracks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        var tracks = await ReadTracksAsync(cmd, cancellationToken);
        return tracks.Count > 0 ? tracks[0] : null;
    }

    public async Task<LibraryTrack?> GetTrackByPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM tracks WHERE file_path = @path";
        cmd.Parameters.AddWithValue("@path", Path.GetFullPath(filePath));

        var tracks = await ReadTracksAsync(cmd, cancellationToken);
        return tracks.Count > 0 ? tracks[0] : null;
    }

    public async Task<IReadOnlyList<LibraryTrack>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM tracks
            WHERE title LIKE @q OR artist LIKE @q OR album LIKE @q OR album_artist LIKE @q
            ORDER BY title COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        return await ReadTracksAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetArtistsAsync(CancellationToken cancellationToken = default)
    {
        return await GetDistinctValuesAsync("artist", cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetAlbumsAsync(
        string? artist = null, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        if (artist is not null)
        {
            cmd.CommandText = """
                SELECT DISTINCT album FROM tracks
                WHERE album IS NOT NULL AND (artist = @artist OR album_artist = @artist)
                ORDER BY album COLLATE NOCASE
                """;
            cmd.Parameters.AddWithValue("@artist", artist);
        }
        else
        {
            cmd.CommandText = """
                SELECT DISTINCT album FROM tracks
                WHERE album IS NOT NULL
                ORDER BY album COLLATE NOCASE
                """;
        }

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    public async Task<IReadOnlyList<string>> GetGenresAsync(CancellationToken cancellationToken = default)
    {
        return await GetDistinctValuesAsync("genre", cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryTrack>> GetTracksByArtistAsync(
        string artist, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM tracks
            WHERE artist = @artist OR album_artist = @artist
            ORDER BY album COLLATE NOCASE, disc_number, track_number
            """;
        cmd.Parameters.AddWithValue("@artist", artist);
        return await ReadTracksAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryTrack>> GetTracksByAlbumAsync(
        string album, string? artist = null, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        if (artist is not null)
        {
            cmd.CommandText = """
                SELECT * FROM tracks
                WHERE album = @album AND (artist = @artist OR album_artist = @artist)
                ORDER BY disc_number, track_number
                """;
            cmd.Parameters.AddWithValue("@artist", artist);
        }
        else
        {
            cmd.CommandText = """
                SELECT * FROM tracks WHERE album = @album
                ORDER BY disc_number, track_number
                """;
        }
        cmd.Parameters.AddWithValue("@album", album);
        return await ReadTracksAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryTrack>> GetTracksByGenreAsync(
        string genre, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM tracks WHERE genre = @genre
            ORDER BY artist COLLATE NOCASE, album COLLATE NOCASE, track_number
            """;
        cmd.Parameters.AddWithValue("@genre", genre);
        return await ReadTracksAsync(cmd, cancellationToken);
    }

    public async Task<LibraryTrack> UpsertTrackAsync(
        LibraryTrack track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tracks (
                file_path, file_size, last_modified_ticks, date_added_ticks,
                title, artist, album, album_artist,
                track_number, track_count, disc_number, year, genre,
                duration_ms, bitrate, sample_rate, channels, codec
            ) VALUES (
                @file_path, @file_size, @last_modified_ticks, @date_added_ticks,
                @title, @artist, @album, @album_artist,
                @track_number, @track_count, @disc_number, @year, @genre,
                @duration_ms, @bitrate, @sample_rate, @channels, @codec
            )
            ON CONFLICT(file_path) DO UPDATE SET
                file_size = excluded.file_size,
                last_modified_ticks = excluded.last_modified_ticks,
                title = excluded.title,
                artist = excluded.artist,
                album = excluded.album,
                album_artist = excluded.album_artist,
                track_number = excluded.track_number,
                track_count = excluded.track_count,
                disc_number = excluded.disc_number,
                year = excluded.year,
                genre = excluded.genre,
                duration_ms = excluded.duration_ms,
                bitrate = excluded.bitrate,
                sample_rate = excluded.sample_rate,
                channels = excluded.channels,
                codec = excluded.codec
            RETURNING id
            """;

        AddTrackParameters(cmd, track);

        var id = await cmd.ExecuteScalarAsync(cancellationToken);
        track.Id = Convert.ToInt64(id);
        return track;
    }

    public async Task BatchUpsertTracksAsync(
        IReadOnlyList<LibraryTrack> tracks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0) return;

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = """
                INSERT INTO tracks (
                    file_path, file_size, last_modified_ticks, date_added_ticks,
                    title, artist, album, album_artist,
                    track_number, track_count, disc_number, year, genre,
                    duration_ms, bitrate, sample_rate, channels, codec
                ) VALUES (
                    @file_path, @file_size, @last_modified_ticks, @date_added_ticks,
                    @title, @artist, @album, @album_artist,
                    @track_number, @track_count, @disc_number, @year, @genre,
                    @duration_ms, @bitrate, @sample_rate, @channels, @codec
                )
                ON CONFLICT(file_path) DO UPDATE SET
                    file_size = excluded.file_size,
                    last_modified_ticks = excluded.last_modified_ticks,
                    title = excluded.title,
                    artist = excluded.artist,
                    album = excluded.album,
                    album_artist = excluded.album_artist,
                    track_number = excluded.track_number,
                    track_count = excluded.track_count,
                    disc_number = excluded.disc_number,
                    year = excluded.year,
                    genre = excluded.genre,
                    duration_ms = excluded.duration_ms,
                    bitrate = excluded.bitrate,
                    sample_rate = excluded.sample_rate,
                    channels = excluded.channels,
                    codec = excluded.codec
                RETURNING id
                """;

            AddTrackParameters(cmd, track);

            var id = await cmd.ExecuteScalarAsync(cancellationToken);
            track.Id = Convert.ToInt64(id);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveTrackAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tracks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RemoveMissingTracksAsync(CancellationToken cancellationToken = default)
    {
        var allTracks = await GetAllTracksAsync(cancellationToken: cancellationToken);

        // Check file existence in parallel to avoid serial I/O stalls
        // (especially on network drives where each File.Exists can block).
        var missingIds = await Task.Run(() =>
            allTracks
                .AsParallel()
                .WithCancellation(cancellationToken)
                .Where(t => !File.Exists(t.FilePath))
                .Select(t => t.Id)
                .ToList(),
            cancellationToken);

        if (missingIds.Count == 0) return 0;

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        // SQLite doesn't support array parameters, so batch delete.
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

        foreach (var id in missingIds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = "DELETE FROM tracks WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return missingIds.Count;
    }

    public async Task<IReadOnlyList<string>> GetWatchedFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT folder_path FROM watched_folders ORDER BY folder_path";

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    public async Task AddWatchedFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        var fullPath = Path.GetFullPath(folderPath);

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO watched_folders (folder_path) VALUES (@path)";
        cmd.Parameters.AddWithValue("@path", fullPath);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveWatchedFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        var fullPath = Path.GetFullPath(folderPath);

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM watched_folders WHERE folder_path = @path";
        cmd.Parameters.AddWithValue("@path", fullPath);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetDistinctValuesAsync(
        string column, CancellationToken cancellationToken)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT {column} FROM tracks WHERE {column} IS NOT NULL ORDER BY {column} COLLATE NOCASE";

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    private static async Task<List<LibraryTrack>> ReadTracksAsync(
        SqliteCommand cmd, CancellationToken cancellationToken)
    {
        var tracks = new List<LibraryTrack>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            tracks.Add(MapTrack(reader));
        }

        return tracks;
    }

    private static LibraryTrack MapTrack(SqliteDataReader reader)
    {
        return new LibraryTrack
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
            LastModifiedTicks = reader.GetInt64(reader.GetOrdinal("last_modified_ticks")),
            DateAddedTicks = reader.GetInt64(reader.GetOrdinal("date_added_ticks")),
            Title = GetNullableString(reader, "title"),
            Artist = GetNullableString(reader, "artist"),
            Album = GetNullableString(reader, "album"),
            AlbumArtist = GetNullableString(reader, "album_artist"),
            TrackNumber = GetNullableUInt(reader, "track_number"),
            TrackCount = GetNullableUInt(reader, "track_count"),
            DiscNumber = GetNullableUInt(reader, "disc_number"),
            Year = GetNullableUInt(reader, "year"),
            Genre = GetNullableString(reader, "genre"),
            DurationMs = GetNullableLong(reader, "duration_ms"),
            Bitrate = GetNullableInt(reader, "bitrate"),
            SampleRate = GetNullableInt(reader, "sample_rate"),
            Channels = GetNullableInt(reader, "channels"),
            Codec = GetNullableString(reader, "codec"),
        };
    }

    private static void AddTrackParameters(SqliteCommand cmd, LibraryTrack track)
    {
        cmd.Parameters.AddWithValue("@file_path", Path.GetFullPath(track.FilePath));
        cmd.Parameters.AddWithValue("@file_size", track.FileSize);
        cmd.Parameters.AddWithValue("@last_modified_ticks", track.LastModifiedTicks);
        cmd.Parameters.AddWithValue("@date_added_ticks", track.DateAddedTicks);
        cmd.Parameters.AddWithValue("@title", (object?)track.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artist", (object?)track.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@album", (object?)track.Album ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@album_artist", (object?)track.AlbumArtist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@track_number", track.TrackNumber.HasValue ? (object)track.TrackNumber.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@track_count", track.TrackCount.HasValue ? (object)track.TrackCount.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@disc_number", track.DiscNumber.HasValue ? (object)track.DiscNumber.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@year", track.Year.HasValue ? (object)track.Year.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@genre", (object?)track.Genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@duration_ms", track.DurationMs.HasValue ? (object)track.DurationMs.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@bitrate", track.Bitrate.HasValue ? (object)track.Bitrate.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@sample_rate", track.SampleRate.HasValue ? (object)track.SampleRate.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@channels", track.Channels.HasValue ? (object)track.Channels.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@codec", (object?)track.Codec ?? DBNull.Value);
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static uint? GetNullableUInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : (uint)reader.GetInt64(ordinal);
    }

    private static int? GetNullableInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? GetNullableLong(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Connection pooling handles cleanup. No persistent connections to dispose.
    }
}
