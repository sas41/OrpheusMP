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

        // ── Core schema ──────────────────────────────────────────────────────
        using (var cmd = conn.CreateCommand())
        {
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

        // ── FTS5 migration ───────────────────────────────────────────────────
        // Check whether tracks_fts already exists. We cannot use
        // CREATE VIRTUAL TABLE IF NOT EXISTS and then trust the triggers also
        // exist, because on databases created before this schema version the
        // table may be absent even though IF NOT EXISTS would silently succeed
        // on a fresh DB. We handle both cases explicitly.
        bool ftsExists;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='tracks_fts'";
            ftsExists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        if (!ftsExists)
        {
            // Create the FTS5 virtual table, the sync triggers, and populate
            // it from whatever rows are already in tracks.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE VIRTUAL TABLE tracks_fts USING fts5(
                    title,
                    artist,
                    album,
                    album_artist,
                    file_path,
                    content=tracks,
                    content_rowid=id,
                    tokenize='unicode61 remove_diacritics 1'
                );

                CREATE TRIGGER tracks_ai AFTER INSERT ON tracks BEGIN
                    INSERT INTO tracks_fts(rowid, title, artist, album, album_artist, file_path)
                    VALUES (new.id, new.title, new.artist, new.album, new.album_artist, new.file_path);
                END;

                CREATE TRIGGER tracks_ad AFTER DELETE ON tracks BEGIN
                    INSERT INTO tracks_fts(tracks_fts, rowid, title, artist, album, album_artist, file_path)
                    VALUES ('delete', old.id, old.title, old.artist, old.album, old.album_artist, old.file_path);
                END;

                CREATE TRIGGER tracks_au AFTER UPDATE ON tracks BEGIN
                    INSERT INTO tracks_fts(tracks_fts, rowid, title, artist, album, album_artist, file_path)
                    VALUES ('delete', old.id, old.title, old.artist, old.album, old.album_artist, old.file_path);
                    INSERT INTO tracks_fts(rowid, title, artist, album, album_artist, file_path)
                    VALUES (new.id, new.title, new.artist, new.album, new.album_artist, new.file_path);
                END;

                -- Backfill FTS index from any pre-existing tracks rows.
                INSERT INTO tracks_fts(rowid, title, artist, album, album_artist, file_path)
                SELECT id, title, artist, album, album_artist, file_path FROM tracks;
                """;
            cmd.ExecuteNonQuery();
        }
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

    public async Task<Dictionary<string, (long FileSize, long LastModifiedTicks)>> GetTrackedFileSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path, file_size, last_modified_ticks FROM tracks";

        var snapshot = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshot[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        }
        return snapshot;
    }

    public async Task<IReadOnlyList<LibraryTrack>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        // ── Stage 1: FTS5 exact/prefix search ────────────────────────────────
        // Returns tracks where every query token matches as a prefix somewhere
        // in any indexed field. Fast and ranked by FTS5 relevance (rank ≈ 0).
        var ftsQuery = BuildFtsQuery(query);
        List<LibraryTrack> ftsResults;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT t.* FROM tracks t
                INNER JOIN tracks_fts ON tracks_fts.rowid = t.id
                WHERE tracks_fts MATCH @q
                ORDER BY rank, t.title COLLATE NOCASE
                """;
            cmd.Parameters.AddWithValue("@q", ftsQuery);
            ftsResults = await ReadTracksAsync(cmd, cancellationToken);
        }

        // ── Stage 2: fuzzy in-memory search over all tracks ──────────────────
        // Scores every track by word-level edit distance so that typos
        // (e.g. "Radiohed" → "Radiohead") still surface results. Lower score
        // is better; score 0 is reserved for FTS5 hits above.
        List<LibraryTrack> allTracks;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM tracks";
            allTracks = await ReadTracksAsync(cmd, cancellationToken);
        }

        var queryTokens = query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var ftsIds = new HashSet<long>(ftsResults.Select(t => t.Id));

        // Score each track; skip tracks already in FTS results.
        var fuzzyScored = new List<(LibraryTrack Track, int Score)>();
        foreach (var track in allTracks)
        {
            if (ftsIds.Contains(track.Id)) continue;

            var score = FuzzyScore(query, queryTokens, track);
            if (score >= 0)
                fuzzyScored.Add((track, score));
        }

        // ── Merge: FTS first (score 0 conceptually), then fuzzy by score ─────
        var fuzzyOrdered = fuzzyScored
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Track.Title ?? "")
            .Select(x => x.Track);

        return ftsResults.Concat(fuzzyOrdered).ToList();
    }

    // Minimum query token length considered for fuzzy matching.
    // Tokens shorter than this (e.g. "to", "in", "a") are stopword-like and
    // would match too many unrelated tracks.
    private const int FuzzyMinTokenLength = 3;

    /// <summary>
    /// Returns a fuzzy score for a track against the query tokens, or -1 if
    /// the track should not appear in results.
    ///
    /// Score semantics: lower is better; 0 means every evaluated token matched
    /// exactly (FTS5 would have caught this, but the path is harmless). -1 means
    /// at least one token found no close match anywhere in the track.
    ///
    /// Algorithm per token:
    ///   1. Skip tokens shorter than <see cref="FuzzyMinTokenLength"/> (stopwords).
    ///   2. Compute a single tolerance from the full query length
    ///      (see <see cref="FuzzyTolerance"/>). This means a longer query like
    ///      "again we test" earns enough tolerance for individual short tokens
    ///      (e.g. "test") to fuzzy-match nearby words (e.g. "rest"), while a
    ///      bare "test" query keeps tolerance 0 and requires exact matches.
    ///   3. For every word in every searchable field, compute Levenshtein distance.
    ///   4. Accept the token only if its best raw distance ≤ tolerance.
    ///   5. Accumulate best_dist * field_weight into the total score.
    ///
    /// Field weights (lower = ranked higher):
    ///   FilePath = 1, Title = 2, Artist = 3, Album = 4, AlbumArtist = 5
    /// </summary>
    private static int FuzzyScore(string query, string[] queryTokens, LibraryTrack track)
    {
        // Only consider tokens long enough to carry signal.
        var evalTokens = Array.FindAll(queryTokens, t => t.Length >= FuzzyMinTokenLength);
        if (evalTokens.Length == 0) return -1;

        // Tolerance is derived from the full query length, not per-token length.
        // A multi-word query earns more tolerance, allowing individual short tokens
        // to fuzzy-match, while a single short query stays exact.
        var tolerance = FuzzyTolerance(query.Length);

        // For filepath, only the filename stem (no extension, no directory) is
        // meaningful for fuzzy matching — the folder path is irrelevant noise.
        // e.g. "/music/2024/TEST - track.flac" → tokens ["test", "track"]
        var filenameStem = Path.GetFileNameWithoutExtension(track.FilePath);

        Span<(string? Value, int Weight)> fields =
        [
            (filenameStem,      1),   // filename stem — highest priority
            (track.Title,       2),
            (track.Artist,      3),
            (track.Album,       4),
            (track.AlbumArtist, 5),
        ];

        var totalScore = 0;

        foreach (var queryToken in evalTokens)
        {
            var tokenLen = queryToken.Length;

            var bestRawDist = int.MaxValue;
            var bestWeight  = int.MaxValue;

            foreach (var (value, weight) in fields)
            {
                if (string.IsNullOrEmpty(value)) continue;

                // Filename stems use additional separators common in file naming
                // conventions; regular metadata fields split on spaces only.
                var words = weight == 1
                    ? value.ToLowerInvariant().Split(
                        [' ', '_', '-', '.'],
                        StringSplitOptions.RemoveEmptyEntries)
                    : value.ToLowerInvariant().Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries);

                foreach (var word in words)
                {
                    // Only compare words whose length is within tolerance of the
                    // query token. This is a strict symmetric window — we do NOT
                    // do fuzzy-prefix matching here because FTS5 already handles
                    // prefix queries, and fuzzy-prefix produces too many false
                    // positives (e.g. "test" matching "restore" via prefix "rest").
                    if (Math.Abs(word.Length - tokenLen) > tolerance) continue;

                    var dist = LevenshteinDistance(queryToken, word);
                    if (dist < bestRawDist || (dist == bestRawDist && weight < bestWeight))
                    {
                        bestRawDist = dist;
                        bestWeight  = weight;
                    }
                }
            }

            if (bestRawDist > tolerance)
                return -1; // this token matched nothing — disqualify the track

            totalScore += bestRawDist * bestWeight;
        }

        return totalScore;
    }

    /// <summary>
    /// Returns the maximum Levenshtein edit distance allowed for a fuzzy match,
    /// based on the length of the query token.
    ///
    /// Short tokens require an exact match to avoid false positives
    /// (e.g. "test" must not match "best", "rest", "lest").
    /// Longer tokens allow progressively more edits for typo tolerance.
    ///
    ///   len 1–4  → 0        (exact match only)
    ///   len 5–6  → 1        (one typo — e.g. "Bohemien" → "Bohemian")
    ///   len 7+   → min(3, len - 5)  (e.g. len 7 → 2, len 8 → 3, len 9+ → 3)
    ///
    /// The cap of 3 prevents multi-word queries whose total length is large
    /// (e.g. "again we test", len=13) from earning absurdly high tolerance that
    /// would allow any token to match almost anything.
    /// </summary>
    private static int FuzzyTolerance(int tokenLength) =>
        tokenLength < 5 ? 0 :
        tokenLength < 7 ? 1 :
        Math.Min(3, tokenLength - 5);

    /// <summary>
    /// Standard Levenshtein distance, O(m) space.
    /// </summary>
    public static int LevenshteinDistance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var dp = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) dp[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            var prev = dp[0];
            dp[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var temp = dp[j];
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[j] = Math.Min(Math.Min(dp[j] + 1, dp[j - 1] + 1), prev + cost);
                prev = temp;
            }
        }

        return dp[b.Length];
    }

    /// <summary>
    /// Converts a user query into an FTS5 MATCH expression.
    /// Each token is quoted and suffixed with * for prefix matching.
    /// e.g. "bohemian rhaps" → "\"bohemian\"* \"rhaps\"*"
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        var tokens = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", tokens.Select(t => $"\"{EscapeFtsToken(t)}\"*"));
    }

    /// <summary>
    /// Escapes double-quote characters inside an FTS5 token (double them).
    /// </summary>
    private static string EscapeFtsToken(string token) => token.Replace("\"", "\"\"");

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

        // Prepare the command once and rebind parameters for each track.
        // Creating a new SqliteCommand per row inside the loop allocates a
        // fresh object (plus parameter collection) for every track, adding GC
        // pressure proportional to the library size.
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

        // Pre-create all parameter slots once; values are overwritten per row.
        AddTrackParameters(cmd, tracks[0]);
        await cmd.PrepareAsync(cancellationToken);

        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateTrackParameters(cmd, track);
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

    public async Task<int> RemoveTracksUnderFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        // Ensure the prefix ends with the directory separator so we don't
        // accidentally match a folder like /music/pop when asked to remove /music/po.
        var fullPath = Path.GetFullPath(folderPath);
        var prefix = fullPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;

        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        // Match tracks stored directly in the folder or any sub-folder.
        cmd.CommandText = "DELETE FROM tracks WHERE file_path LIKE @prefix ESCAPE '\\'";
        cmd.Parameters.AddWithValue("@prefix", EscapeLike(prefix) + "%");
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Escapes LIKE special characters in a literal path prefix.</summary>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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

    /// <summary>
    /// Rebinds the values of parameters that were already added via
    /// <see cref="AddTrackParameters"/>. Used by <see cref="BatchUpsertTracksAsync"/>
    /// to reuse a single prepared <see cref="SqliteCommand"/> across all rows in
    /// a batch, avoiding per-row command/parameter allocation.
    /// </summary>
    private static void UpdateTrackParameters(SqliteCommand cmd, LibraryTrack track)
    {
        cmd.Parameters["@file_path"].Value         = Path.GetFullPath(track.FilePath);
        cmd.Parameters["@file_size"].Value         = track.FileSize;
        cmd.Parameters["@last_modified_ticks"].Value = track.LastModifiedTicks;
        cmd.Parameters["@date_added_ticks"].Value  = track.DateAddedTicks;
        cmd.Parameters["@title"].Value             = (object?)track.Title ?? DBNull.Value;
        cmd.Parameters["@artist"].Value            = (object?)track.Artist ?? DBNull.Value;
        cmd.Parameters["@album"].Value             = (object?)track.Album ?? DBNull.Value;
        cmd.Parameters["@album_artist"].Value      = (object?)track.AlbumArtist ?? DBNull.Value;
        cmd.Parameters["@track_number"].Value      = track.TrackNumber.HasValue ? (object)track.TrackNumber.Value : DBNull.Value;
        cmd.Parameters["@track_count"].Value       = track.TrackCount.HasValue  ? (object)track.TrackCount.Value  : DBNull.Value;
        cmd.Parameters["@disc_number"].Value       = track.DiscNumber.HasValue  ? (object)track.DiscNumber.Value  : DBNull.Value;
        cmd.Parameters["@year"].Value              = track.Year.HasValue         ? (object)track.Year.Value        : DBNull.Value;
        cmd.Parameters["@genre"].Value             = (object?)track.Genre ?? DBNull.Value;
        cmd.Parameters["@duration_ms"].Value       = track.DurationMs.HasValue  ? (object)track.DurationMs.Value  : DBNull.Value;
        cmd.Parameters["@bitrate"].Value           = track.Bitrate.HasValue      ? (object)track.Bitrate.Value     : DBNull.Value;
        cmd.Parameters["@sample_rate"].Value       = track.SampleRate.HasValue   ? (object)track.SampleRate.Value  : DBNull.Value;
        cmd.Parameters["@channels"].Value          = track.Channels.HasValue     ? (object)track.Channels.Value    : DBNull.Value;
        cmd.Parameters["@codec"].Value             = (object?)track.Codec ?? DBNull.Value;
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

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var conn = OpenConnection();
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Drop and recreate the FTS index instead of deleting row-by-row via
        // triggers. This avoids the trigger firing against a potentially stale
        // or missing FTS shadow table, and is significantly faster for large
        // libraries. The triggers are also recreated so they are ready for the
        // next scan.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = """
                DROP TRIGGER IF EXISTS tracks_ai;
                DROP TRIGGER IF EXISTS tracks_ad;
                DROP TRIGGER IF EXISTS tracks_au;
                DROP TABLE IF EXISTS tracks_fts;

                DELETE FROM tracks;

                CREATE VIRTUAL TABLE tracks_fts USING fts5(
                    title,
                    artist,
                    album,
                    album_artist,
                    file_path,
                    content=tracks,
                    content_rowid=id,
                    tokenize='unicode61 remove_diacritics 1'
                );

                CREATE TRIGGER tracks_ai AFTER INSERT ON tracks BEGIN
                    INSERT INTO tracks_fts(rowid, title, artist, album, album_artist, file_path)
                    VALUES (new.id, new.title, new.artist, new.album, new.album_artist, new.file_path);
                END;

                CREATE TRIGGER tracks_ad AFTER DELETE ON tracks BEGIN
                    INSERT INTO tracks_fts(tracks_fts, rowid, title, artist, album, album_artist, file_path)
                    VALUES ('delete', old.id, old.title, old.artist, old.album, old.album_artist, old.file_path);
                END;

                CREATE TRIGGER tracks_au AFTER UPDATE ON tracks BEGIN
                    INSERT INTO tracks_fts(tracks_fts, rowid, title, artist, album, album_artist, file_path)
                    VALUES ('delete', old.id, old.title, old.artist, old.album, old.album_artist, old.file_path);
                    INSERT INTO tracks_fts(rowid, title, artist, album, album_artist, file_path)
                    VALUES (new.id, new.title, new.artist, new.album, new.album_artist, new.file_path);
                END;
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Connection pooling handles cleanup. No persistent connections to dispose.
    }

}