using Orpheus.Core.Library;

namespace Orpheus.Core.Tests.Library;

public class SqliteMediaLibraryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteMediaLibrary _library;

    public SqliteMediaLibraryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"orpheus_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test_library.db");
        _library = new SqliteMediaLibrary(_dbPath);
    }

    public void Dispose()
    {
        _library.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private LibraryTrack MakeTrack(string filePath, string? title = null, string? artist = null,
        string? album = null, string? genre = null, uint? year = null) =>
        new()
        {
            FilePath = filePath,
            FileSize = 1024,
            LastModifiedTicks = DateTimeOffset.UtcNow.Ticks,
            DateAddedTicks = DateTimeOffset.UtcNow.Ticks,
            Title = title,
            Artist = artist,
            Album = album,
            Genre = genre,
            Year = year,
            DurationMs = 180_000
        };

    [Fact]
    public async Task UpsertTrack_InsertsNewTrack()
    {
        var track = MakeTrack("/music/song.mp3", "Song", "Artist");
        var result = await _library.UpsertTrackAsync(track);

        Assert.True(result.Id > 0);
    }

    [Fact]
    public async Task UpsertTrack_UpdatesExistingTrack()
    {
        var track = MakeTrack("/music/song.mp3", "Old Title", "Artist");
        await _library.UpsertTrackAsync(track);

        track.Title = "New Title";
        var updated = await _library.UpsertTrackAsync(track);

        var fetched = await _library.GetTrackByIdAsync(updated.Id);
        Assert.NotNull(fetched);
        Assert.Equal("New Title", fetched.Title);
    }

    [Fact]
    public async Task GetTrackCount_ReturnsCorrectCount()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "A"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", "B"));
        await _library.UpsertTrackAsync(MakeTrack("/music/c.mp3", "C"));

        var count = await _library.GetTrackCountAsync();
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task GetTrackByPath_FindsTrack()
    {
        var path = Path.Combine(_tempDir, "findme.mp3");
        await _library.UpsertTrackAsync(MakeTrack(path, "Find Me"));

        var found = await _library.GetTrackByPathAsync(path);
        Assert.NotNull(found);
        Assert.Equal("Find Me", found.Title);
    }

    [Fact]
    public async Task GetTrackByPath_ReturnsNullForMissing()
    {
        var found = await _library.GetTrackByPathAsync("/nonexistent/path.mp3");
        Assert.Null(found);
    }

    [Fact]
    public async Task Search_FindsByTitle()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "Bohemian Rhapsody", "Queen"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", "Stairway to Heaven", "Led Zeppelin"));

        var results = await _library.SearchAsync("Bohemian");
        Assert.Single(results);
        Assert.Equal("Bohemian Rhapsody", results[0].Title);
    }

    [Fact]
    public async Task Search_FindsByArtist()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "Song", "Radiohead"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", "Other", "Coldplay"));

        var results = await _library.SearchAsync("Radiohead");
        Assert.Single(results);
    }

    [Fact]
    public async Task GetArtists_ReturnsDistinctArtists()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "A", "Artist1"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", "B", "Artist2"));
        await _library.UpsertTrackAsync(MakeTrack("/music/c.mp3", "C", "Artist1"));

        var artists = await _library.GetArtistsAsync();
        Assert.Equal(2, artists.Count);
    }

    [Fact]
    public async Task GetAlbums_ReturnsDistinctAlbums()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "A", album: "Album1"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", "B", album: "Album2"));
        await _library.UpsertTrackAsync(MakeTrack("/music/c.mp3", "C", album: "Album1"));

        var albums = await _library.GetAlbumsAsync();
        Assert.Equal(2, albums.Count);
    }

    [Fact]
    public async Task GetAlbums_FiltersByArtist()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "A", "ArtistA", "AlbumA"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", "B", "ArtistB", "AlbumB"));

        var albums = await _library.GetAlbumsAsync("ArtistA");
        Assert.Single(albums);
        Assert.Equal("AlbumA", albums[0]);
    }

    [Fact]
    public async Task GetGenres_ReturnsDistinctGenres()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", genre: "Rock"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", genre: "Jazz"));
        await _library.UpsertTrackAsync(MakeTrack("/music/c.mp3", genre: "Rock"));

        var genres = await _library.GetGenresAsync();
        Assert.Equal(2, genres.Count);
    }

    [Fact]
    public async Task GetTracksByArtist_ReturnsCorrectTracks()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "A", "Queen"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", "B", "Queen"));
        await _library.UpsertTrackAsync(MakeTrack("/music/c.mp3", "C", "Beatles"));

        var tracks = await _library.GetTracksByArtistAsync("Queen");
        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public async Task GetTracksByAlbum_ReturnsCorrectTracks()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "A", album: "Abbey Road"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", "B", album: "Abbey Road"));
        await _library.UpsertTrackAsync(MakeTrack("/music/c.mp3", "C", album: "Other"));

        var tracks = await _library.GetTracksByAlbumAsync("Abbey Road");
        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public async Task GetTracksByGenre_ReturnsCorrectTracks()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "A", genre: "Rock"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", "B", genre: "Jazz"));

        var tracks = await _library.GetTracksByGenreAsync("Rock");
        Assert.Single(tracks);
    }

    [Fact]
    public async Task RemoveTrack_RemovesById()
    {
        var track = await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "A"));
        await _library.RemoveTrackAsync(track.Id);

        var count = await _library.GetTrackCountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetAllTracks_SortsByTitle()
    {
        await _library.UpsertTrackAsync(MakeTrack("/music/c.mp3", "Charlie"));
        await _library.UpsertTrackAsync(MakeTrack("/music/a.mp3", "Alpha"));
        await _library.UpsertTrackAsync(MakeTrack("/music/b.mp3", "Bravo"));

        var tracks = await _library.GetAllTracksAsync(TrackSortOrder.Title);
        Assert.Equal("Alpha", tracks[0].Title);
        Assert.Equal("Bravo", tracks[1].Title);
        Assert.Equal("Charlie", tracks[2].Title);
    }

    [Fact]
    public async Task WatchedFolders_AddAndRetrieve()
    {
        await _library.AddWatchedFolderAsync(_tempDir);

        var folders = await _library.GetWatchedFoldersAsync();
        Assert.Single(folders);
        Assert.Equal(Path.GetFullPath(_tempDir), folders[0]);
    }

    [Fact]
    public async Task WatchedFolders_DuplicateAddIsIgnored()
    {
        await _library.AddWatchedFolderAsync(_tempDir);
        await _library.AddWatchedFolderAsync(_tempDir);

        var folders = await _library.GetWatchedFoldersAsync();
        Assert.Single(folders);
    }

    [Fact]
    public async Task WatchedFolders_RemoveWorks()
    {
        await _library.AddWatchedFolderAsync(_tempDir);
        await _library.RemoveWatchedFolderAsync(_tempDir);

        var folders = await _library.GetWatchedFoldersAsync();
        Assert.Empty(folders);
    }

    [Fact]
    public async Task LibraryTrack_ToString_FormatsCorrectly()
    {
        var track = MakeTrack("/music/song.mp3", "Bohemian Rhapsody", "Queen");
        Assert.Equal("Queen - Bohemian Rhapsody", track.ToString());

        var titleOnly = MakeTrack("/music/song.mp3", "Untitled");
        Assert.Equal("Untitled", titleOnly.ToString());

        var noMetadata = MakeTrack("/music/song.mp3");
        Assert.Equal("song", noMetadata.ToString());
    }

    [Fact]
    public async Task LibraryTrack_Duration_ConvertsFromMs()
    {
        var track = MakeTrack("/music/song.mp3");
        track.DurationMs = 180_000;

        Assert.NotNull(track.Duration);
        Assert.Equal(TimeSpan.FromMinutes(3), track.Duration.Value);
    }
}
