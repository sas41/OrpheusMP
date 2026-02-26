using NSubstitute;
using Orpheus.Core.Media;
using Orpheus.Core.Playback;
using Orpheus.Core.Playlist;

namespace Orpheus.Core.Tests.Playback;

public class PlayerControllerTests : IAsyncLifetime
{
    private readonly IPlayer _mockPlayer;
    private readonly PlayerController _controller;

    public PlayerControllerTests()
    {
        _mockPlayer = Substitute.For<IPlayer>();
        _mockPlayer.State.Returns(PlaybackState.Stopped);
        _controller = new PlayerController(_mockPlayer);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _controller.DisposeAsync();
    }

    private static PlaylistItem MakeItem(string name) =>
        new() { Source = MediaSource.FromUri($"http://example.com/{name}.mp3") };

    private void LoadPlaylist(int count)
    {
        for (var i = 0; i < count; i++)
            _controller.Playlist.Add(MakeItem($"track{i}"));
        _controller.Playlist.CurrentIndex = 0;
    }

    // ── Basic playback ────────────────────────────────────────────────

    [Fact]
    public async Task PlayAsync_StartsFromBeginningWhenNoCurrentIndex()
    {
        _controller.Playlist.Add(MakeItem("track1"));
        _controller.Playlist.Add(MakeItem("track2"));

        await _controller.PlayAsync();

        Assert.Equal(0, _controller.Playlist.CurrentIndex);
        await _mockPlayer.Received(1).PlayAsync(
            Arg.Any<MediaSource>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlayAsync_DoesNothingWhenPlaylistEmpty()
    {
        await _controller.PlayAsync();

        await _mockPlayer.DidNotReceive().PlayAsync(
            Arg.Any<MediaSource>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlayAtIndexAsync_SetsCurrentIndex()
    {
        LoadPlaylist(3);

        await _controller.PlayAtIndexAsync(2);

        Assert.Equal(2, _controller.Playlist.CurrentIndex);
        await _mockPlayer.Received().PlayAsync(
            Arg.Any<MediaSource>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlayAtIndexAsync_ThrowsOnInvalidIndex()
    {
        _controller.Playlist.Add(MakeItem("a"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _controller.PlayAtIndexAsync(5));
    }

    [Fact]
    public async Task PauseAsync_DelegatesToPlayer()
    {
        await _controller.PauseAsync();
        await _mockPlayer.Received(1).PauseAsync();
    }

    [Fact]
    public async Task ResumeAsync_DelegatesToPlayer()
    {
        await _controller.ResumeAsync();
        await _mockPlayer.Received(1).ResumeAsync();
    }

    [Fact]
    public async Task StopAsync_DelegatesToPlayer()
    {
        await _controller.StopAsync();
        await _mockPlayer.Received(1).StopAsync();
    }

    [Fact]
    public async Task TogglePlayPauseAsync_PausesWhenPlaying()
    {
        _mockPlayer.State.Returns(PlaybackState.Playing);
        await _controller.TogglePlayPauseAsync();
        await _mockPlayer.Received(1).PauseAsync();
    }

    [Fact]
    public async Task TogglePlayPauseAsync_ResumesWhenPaused()
    {
        _mockPlayer.State.Returns(PlaybackState.Paused);
        await _controller.TogglePlayPauseAsync();
        await _mockPlayer.Received(1).ResumeAsync();
    }

    [Fact]
    public async Task TogglePlayPauseAsync_PlaysWhenStopped()
    {
        _mockPlayer.State.Returns(PlaybackState.Stopped);
        _controller.Playlist.Add(MakeItem("a"));

        await _controller.TogglePlayPauseAsync();

        await _mockPlayer.Received(1).PlayAsync(
            Arg.Any<MediaSource>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AutoAdvance_DefaultsToTrue()
    {
        Assert.True(_controller.AutoAdvance);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromPlayerEvents()
    {
        await _controller.DisposeAsync();
        await _mockPlayer.Received(1).DisposeAsync();
    }

    // ── Repeat Off ────────────────────────────────────────────────────

    [Fact]
    public async Task RepeatOff_NextAsync_AdvancesSequentially()
    {
        LoadPlaylist(3);
        _controller.RepeatMode = RepeatMode.Off;

        await _controller.NextAsync();
        Assert.Equal(1, _controller.Playlist.CurrentIndex);

        await _controller.NextAsync();
        Assert.Equal(2, _controller.Playlist.CurrentIndex);
    }

    [Fact]
    public async Task RepeatOff_NextAsync_StopsAtEnd()
    {
        LoadPlaylist(2);
        _controller.RepeatMode = RepeatMode.Off;
        _controller.Playlist.CurrentIndex = 1;

        await _controller.NextAsync();

        await _mockPlayer.Received().StopAsync();
    }

    [Fact]
    public async Task RepeatOff_PreviousAsync_RestartsTrackIfPast3Seconds()
    {
        _mockPlayer.Position.Returns(TimeSpan.FromSeconds(5));
        LoadPlaylist(3);
        _controller.Playlist.CurrentIndex = 1;

        await _controller.PreviousAsync();

        await _mockPlayer.Received(1).SeekAsync(TimeSpan.Zero);
        Assert.Equal(1, _controller.Playlist.CurrentIndex);
    }

    [Fact]
    public async Task RepeatOff_PreviousAsync_GoesBackIfEarlyInTrack()
    {
        _mockPlayer.Position.Returns(TimeSpan.FromSeconds(1));
        LoadPlaylist(3);
        _controller.Playlist.CurrentIndex = 1;

        await _controller.PreviousAsync();

        Assert.Equal(0, _controller.Playlist.CurrentIndex);
    }

    [Fact]
    public async Task RepeatOff_PreviousAsync_RestartsAtStart()
    {
        _mockPlayer.Position.Returns(TimeSpan.FromSeconds(1));
        LoadPlaylist(3);
        _controller.Playlist.CurrentIndex = 0;

        await _controller.PreviousAsync();

        await _mockPlayer.Received().SeekAsync(TimeSpan.Zero);
    }

    // ── Repeat One ────────────────────────────────────────────────────

    [Fact]
    public async Task RepeatOne_NextAsync_StaysOnSameTrack()
    {
        LoadPlaylist(3);
        _controller.RepeatMode = RepeatMode.One;
        _controller.Playlist.CurrentIndex = 1;

        await _controller.NextAsync();

        Assert.Equal(1, _controller.Playlist.CurrentIndex);
        await _mockPlayer.Received().PlayAsync(
            Arg.Any<MediaSource>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepeatOne_PreviousAsync_StaysOnSameTrack()
    {
        _mockPlayer.Position.Returns(TimeSpan.FromSeconds(0));
        LoadPlaylist(3);
        _controller.RepeatMode = RepeatMode.One;
        _controller.Playlist.CurrentIndex = 1;

        await _controller.PreviousAsync();

        Assert.Equal(1, _controller.Playlist.CurrentIndex);
    }

    // ── Repeat All (Playlist) ─────────────────────────────────────────

    [Fact]
    public async Task RepeatAll_NextAsync_WrapsToStart()
    {
        LoadPlaylist(3);
        _controller.RepeatMode = RepeatMode.All;
        _controller.Playlist.CurrentIndex = 2;

        await _controller.NextAsync();

        Assert.Equal(0, _controller.Playlist.CurrentIndex);
    }

    [Fact]
    public async Task RepeatAll_PreviousAsync_WrapsToEnd()
    {
        _mockPlayer.Position.Returns(TimeSpan.FromSeconds(0));
        LoadPlaylist(3);
        _controller.RepeatMode = RepeatMode.All;
        _controller.Playlist.CurrentIndex = 0;

        await _controller.PreviousAsync();

        Assert.Equal(2, _controller.Playlist.CurrentIndex);
    }

    // ── CycleRepeatMode ───────────────────────────────────────────────

    [Fact]
    public void CycleRepeatMode_CyclesOffAllOneOff()
    {
        Assert.Equal(RepeatMode.Off, _controller.RepeatMode);

        var mode = _controller.CycleRepeatMode();
        Assert.Equal(RepeatMode.All, mode);

        mode = _controller.CycleRepeatMode();
        Assert.Equal(RepeatMode.One, mode);

        mode = _controller.CycleRepeatMode();
        Assert.Equal(RepeatMode.Off, mode);
    }

    [Fact]
    public void CycleRepeatMode_FiresEvent()
    {
        var reported = new List<RepeatMode>();
        _controller.RepeatModeChanged += (_, m) => reported.Add(m);

        _controller.CycleRepeatMode();
        _controller.CycleRepeatMode();
        _controller.CycleRepeatMode();

        Assert.Equal([RepeatMode.All, RepeatMode.One, RepeatMode.Off], reported);
    }

    // ── Shuffle Play ──────────────────────────────────────────────────

    [Fact]
    public void ShufflePlay_DefaultsToFalse()
    {
        Assert.False(_controller.ShufflePlay);
    }

    [Fact]
    public void ToggleShufflePlay_TogglesState()
    {
        var result = _controller.ToggleShufflePlay();
        Assert.True(result);
        Assert.True(_controller.ShufflePlay);

        result = _controller.ToggleShufflePlay();
        Assert.False(result);
        Assert.False(_controller.ShufflePlay);
    }

    [Fact]
    public void ShufflePlay_FiresEvent()
    {
        var reported = new List<bool>();
        _controller.ShufflePlayChanged += (_, v) => reported.Add(v);

        _controller.ShufflePlay = true;
        _controller.ShufflePlay = false;

        Assert.Equal([true, false], reported);
    }

    [Fact]
    public void ShufflePlay_EventDoesNotFireWhenUnchanged()
    {
        _controller.ShufflePlay = true;
        var fired = false;
        _controller.ShufflePlayChanged += (_, _) => fired = true;

        _controller.ShufflePlay = true; // Same value.
        Assert.False(fired);
    }

    [Fact]
    public async Task ShufflePlay_NextAsync_VisitsAllTracks()
    {
        LoadPlaylist(10);
        _controller.ShufflePlay = true;

        var visited = new HashSet<int> { _controller.Playlist.CurrentIndex };
        for (var i = 0; i < 9; i++)
        {
            await _controller.NextAsync();
            visited.Add(_controller.Playlist.CurrentIndex);
        }

        Assert.Equal(10, visited.Count);
    }

    [Fact]
    public async Task ShufflePlay_NextAsync_StopsWhenExhaustedAndRepeatOff()
    {
        LoadPlaylist(3);
        _controller.ShufflePlay = true;
        _controller.RepeatMode = RepeatMode.Off;

        // Exhaust all tracks.
        await _controller.NextAsync();
        await _controller.NextAsync();
        await _controller.NextAsync(); // Should stop.

        await _mockPlayer.Received().StopAsync();
    }

    [Fact]
    public async Task ShufflePlay_NextAsync_ReshufflesOnRepeatAll()
    {
        LoadPlaylist(3);
        _controller.ShufflePlay = true;
        _controller.RepeatMode = RepeatMode.All;

        // Exhaust all tracks.
        await _controller.NextAsync();
        await _controller.NextAsync();

        // Should reshuffle and continue, not stop.
        await _controller.NextAsync();

        await _mockPlayer.DidNotReceive().StopAsync();
    }

    [Fact]
    public async Task ShufflePlay_PreviousAsync_GoesBackInShuffleOrder()
    {
        _mockPlayer.Position.Returns(TimeSpan.FromSeconds(0));
        LoadPlaylist(5);
        _controller.ShufflePlay = true;

        // Move forward twice to establish a history.
        await _controller.NextAsync();
        var afterFirst = _controller.Playlist.CurrentIndex;
        await _controller.NextAsync();

        // Go back.
        await _controller.PreviousAsync();
        Assert.Equal(afterFirst, _controller.Playlist.CurrentIndex);
    }

    // ── UI Helpers: GetUpcomingTracks ──────────────────────────────────

    [Fact]
    public void GetUpcomingTracks_ReturnsSequentialOrder()
    {
        LoadPlaylist(5);
        _controller.Playlist.CurrentIndex = 1;

        var upcoming = _controller.GetUpcomingTracks();

        Assert.Equal(3, upcoming.Count);
        Assert.Same(_controller.Playlist[2], upcoming[0]);
        Assert.Same(_controller.Playlist[3], upcoming[1]);
        Assert.Same(_controller.Playlist[4], upcoming[2]);
    }

    [Fact]
    public void GetUpcomingTracks_WrapsWithRepeatAll()
    {
        LoadPlaylist(5);
        _controller.Playlist.CurrentIndex = 3;
        _controller.RepeatMode = RepeatMode.All;

        var upcoming = _controller.GetUpcomingTracks();

        // Should get: [4, 0, 1, 2] (wraps around, excludes current 3).
        Assert.Equal(4, upcoming.Count);
        Assert.Same(_controller.Playlist[4], upcoming[0]);
        Assert.Same(_controller.Playlist[0], upcoming[1]);
        Assert.Same(_controller.Playlist[1], upcoming[2]);
        Assert.Same(_controller.Playlist[2], upcoming[3]);
    }

    [Fact]
    public void GetUpcomingTracks_RespectsMaxCount()
    {
        LoadPlaylist(50);

        var upcoming = _controller.GetUpcomingTracks(maxCount: 5);

        Assert.Equal(5, upcoming.Count);
    }

    [Fact]
    public void GetUpcomingTracks_ReturnsEmptyForEmptyPlaylist()
    {
        var upcoming = _controller.GetUpcomingTracks();
        Assert.Empty(upcoming);
    }

    [Fact]
    public void GetUpcomingTracks_ShufflePlayReturnsShuffleOrder()
    {
        LoadPlaylist(10);
        _controller.ShufflePlay = true;

        var upcoming = _controller.GetUpcomingTracks();

        // Should return items, and not necessarily in sequential order.
        Assert.True(upcoming.Count > 0);
        Assert.True(upcoming.Count <= 9); // Excludes current track.
    }

    // ── UI Helpers: GetPlaybackPosition ───────────────────────────────

    [Fact]
    public void GetPlaybackPosition_ReturnsCorrectSequentialPosition()
    {
        LoadPlaylist(10);
        _controller.Playlist.CurrentIndex = 3;

        var (current, total) = _controller.GetPlaybackPosition();

        Assert.Equal(4, current); // 1-indexed.
        Assert.Equal(10, total);
    }

    [Fact]
    public void GetPlaybackPosition_ReturnsZeroForEmptyPlaylist()
    {
        var (current, total) = _controller.GetPlaybackPosition();

        Assert.Equal(0, current);
        Assert.Equal(0, total);
    }

    [Fact]
    public void GetPlaybackPosition_ShufflePlayReturnsShufflePosition()
    {
        LoadPlaylist(10);
        _controller.ShufflePlay = true;

        var (current, total) = _controller.GetPlaybackPosition();

        Assert.Equal(1, current); // Current track is at position 0 in shuffle → 1-indexed.
        Assert.Equal(10, total);
    }
}
