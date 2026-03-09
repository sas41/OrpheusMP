using System.Linq;
using Orpheus.Core.Library;

namespace Orpheus.Core.Tests.Library;

public class TrackListProjectionTests
{
    private sealed record FakeTrack(
        string Title,
        string Artist,
        string Album,
        string FileName,
        string TrackNumber,
        string Year,
        string Genre,
        string Bitrate,
        string Duration,
        long? DateAddedTicks = null);

    private static readonly TrackListProjectionSelectors<FakeTrack> Selectors = new(
        track => track.Title,
        track => track.Artist,
        track => track.Album,
        track => track.FileName,
        track => track.TrackNumber,
        track => track.Year,
        track => track.Genre,
        track => track.Bitrate,
        track => track.Duration,
        track => track.DateAddedTicks);

    [Fact]
    public void Apply_SortsAndFilters_UsingProjectionOptions()
    {
        var tracks = new[]
        {
            new FakeTrack("Zulu", "", "Album", "zulu.mp3", "2", "2024", "Rock", "320 kbps", "4:10", 20),
            new FakeTrack("Alpha", "Artist", "Album", "alpha.mp3", "1", "2020", "Rock", "128 kbps", "3:05", 10),
            new FakeTrack("Bravo", "Artist", "Album", "bravo.mp3", "3", "2022", "Rock", "256 kbps", "5:01", 30),
        };

        var result = TrackListProjection.Apply(
            tracks,
            new TrackListProjectionOptions(
                SortField: TrackListSortField.Title,
                SortAscending: true,
                EnableSort: true,
                HideMissingArtist: true),
            Selectors);

        Assert.Equal(new[] { "Alpha", "Bravo" }, result.Select(track => track.Title));
    }

    [Fact]
    public void Apply_PreservesSourceOrder_WhenProjectionSuppressed()
    {
        var tracks = new[]
        {
            new FakeTrack("Zulu", "", "Album", "zulu.mp3", "2", "2024", "", "320 kbps", "4:10", 20),
            new FakeTrack("Alpha", "Artist", "Album", "alpha.mp3", "1", "2020", "Rock", "128 kbps", "3:05", 10),
        };

        var result = TrackListProjection.Apply(
            tracks,
            new TrackListProjectionOptions(
                SortField: TrackListSortField.Title,
                SortAscending: true,
                EnableSort: true,
                SuppressSortAndFilter: true,
                HideMissingArtist: true),
            Selectors);

        Assert.Equal(new[] { "Zulu", "Alpha" }, result.Select(track => track.Title));
    }

    [Fact]
    public void Apply_SortsDateAdded_WhenSelectorAvailable()
    {
        var tracks = new[]
        {
            new FakeTrack("Alpha", "Artist", "Album", "alpha.mp3", "1", "2020", "Rock", "128 kbps", "3:05", 10),
            new FakeTrack("Bravo", "Artist", "Album", "bravo.mp3", "2", "2022", "Rock", "256 kbps", "5:01", 30),
            new FakeTrack("Charlie", "Artist", "Album", "charlie.mp3", "3", "2024", "Rock", "320 kbps", "4:10", 20),
        };

        var result = TrackListProjection.Apply(
            tracks,
            new TrackListProjectionOptions(
                SortField: TrackListSortField.DateAdded,
                SortAscending: false,
                EnableSort: true),
            Selectors);

        Assert.Equal(new[] { "Bravo", "Charlie", "Alpha" }, result.Select(track => track.Title));
    }

    [Fact]
    public void Apply_LeavesDateAddedInSourceOrder_WhenSelectorMissing()
    {
        var tracks = new[]
        {
            new FakeTrack("Zulu", "Artist", "Album", "zulu.mp3", "2", "2024", "Rock", "320 kbps", "4:10"),
            new FakeTrack("Alpha", "Artist", "Album", "alpha.mp3", "1", "2020", "Rock", "128 kbps", "3:05"),
        };

        var selectorsWithoutDate = Selectors with { DateAddedTicks = null };
        var result = TrackListProjection.Apply(
            tracks,
            new TrackListProjectionOptions(
                SortField: TrackListSortField.DateAdded,
                SortAscending: true,
                EnableSort: true),
            selectorsWithoutDate);

        Assert.Equal(new[] { "Zulu", "Alpha" }, result.Select(track => track.Title));
    }
}
