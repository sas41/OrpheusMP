namespace Orpheus.Core.Library;

/// <summary>
/// Media library interface. Manages a database of audio tracks with
/// folder scanning, search, and browsing capabilities.
/// </summary>
public interface IMediaLibrary : IDisposable
{
    /// <summary>
    /// Get the total number of tracks in the library.
    /// </summary>
    Task<int> GetTrackCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all tracks, optionally ordered.
    /// </summary>
    Task<IReadOnlyList<LibraryTrack>> GetAllTracksAsync(
        TrackSortOrder sortOrder = TrackSortOrder.Title,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a track by its database ID.
    /// </summary>
    Task<LibraryTrack?> GetTrackByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a track by its file path.
    /// </summary>
    Task<LibraryTrack?> GetTrackByPathAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search tracks by a query string. Matches against title, artist, album,
    /// album artist, and file path using full-text search with prefix matching.
    /// </summary>
    Task<IReadOnlyList<LibraryTrack>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all distinct artists in the library.
    /// </summary>
    Task<IReadOnlyList<string>> GetArtistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all distinct albums in the library, optionally filtered by artist.
    /// </summary>
    Task<IReadOnlyList<string>> GetAlbumsAsync(
        string? artist = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all distinct genres in the library.
    /// </summary>
    Task<IReadOnlyList<string>> GetGenresAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get tracks filtered by artist.
    /// </summary>
    Task<IReadOnlyList<LibraryTrack>> GetTracksByArtistAsync(
        string artist,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get tracks filtered by album (and optionally artist).
    /// </summary>
    Task<IReadOnlyList<LibraryTrack>> GetTracksByAlbumAsync(
        string album,
        string? artist = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get tracks filtered by genre.
    /// </summary>
    Task<IReadOnlyList<LibraryTrack>> GetTracksByGenreAsync(
        string genre,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Add or update a single track in the library.
    /// </summary>
    Task<LibraryTrack> UpsertTrackAsync(LibraryTrack track, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add or update multiple tracks in a single transaction for bulk performance.
    /// </summary>
    Task BatchUpsertTracksAsync(IReadOnlyList<LibraryTrack> tracks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a track from the library by ID.
    /// </summary>
    Task RemoveTrackAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove tracks whose files no longer exist on disk.
    /// Returns the number of tracks removed.
    /// </summary>
    Task<int> RemoveMissingTracksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the list of monitored folders.
    /// </summary>
    Task<IReadOnlyList<string>> GetWatchedFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a folder to be monitored by the library.
    /// </summary>
    Task AddWatchedFolderAsync(string folderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a folder from monitoring.
    /// </summary>
    Task RemoveWatchedFolderAsync(string folderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all indexed tracks, resetting the library to an empty state.
    /// Watched folders are preserved.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
