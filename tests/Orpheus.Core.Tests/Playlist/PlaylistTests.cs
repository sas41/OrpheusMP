using Orpheus.Core.Media;
using Orpheus.Core.Playlist;

namespace Orpheus.Core.Tests.Playlist;

public class PlaylistTests
{
    private static PlaylistItem MakeItem(string name) =>
        new() { Source = MediaSource.FromUri($"http://example.com/{name}.mp3") };

    // ── Basic collection ──────────────────────────────────────────────

    [Fact]
    public void Add_IncreasesCount()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("track1"));
        playlist.Add(MakeItem("track2"));

        Assert.Equal(2, playlist.Count);
    }

    [Fact]
    public void AddRange_AddsMultipleItems()
    {
        var playlist = new Core.Playlist.Playlist();
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        playlist.AddRange(items);

        Assert.Equal(3, playlist.Count);
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("track1"));
        playlist.Add(MakeItem("track2"));
        playlist.Clear();

        Assert.Empty(playlist);
        Assert.Equal(-1, playlist.CurrentIndex);
    }

    [Fact]
    public void CurrentItem_ReturnsNullWhenEmpty()
    {
        var playlist = new Core.Playlist.Playlist();
        Assert.Null(playlist.CurrentItem);
    }

    [Fact]
    public void CurrentIndex_CanBeSet()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("track1"));
        playlist.Add(MakeItem("track2"));

        playlist.CurrentIndex = 1;
        Assert.Equal(1, playlist.CurrentIndex);
        Assert.NotNull(playlist.CurrentItem);
    }

    [Fact]
    public void CurrentIndex_ThrowsOnOutOfRange()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("track1"));

        Assert.Throws<ArgumentOutOfRangeException>(() => playlist.CurrentIndex = 5);
    }

    // ── Insert / Remove / Move ────────────────────────────────────────

    [Fact]
    public void Insert_InsertsAtCorrectPosition()
    {
        var playlist = new Core.Playlist.Playlist();
        var first = MakeItem("first");
        var second = MakeItem("second");
        var middle = MakeItem("middle");

        playlist.Add(first);
        playlist.Add(second);
        playlist.Insert(1, middle);

        Assert.Equal(3, playlist.Count);
        Assert.Same(middle, playlist[1]);
        Assert.Same(second, playlist[2]);
    }

    [Fact]
    public void Insert_UpdatesCurrentIndexWhenInsertedBefore()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));
        playlist.CurrentIndex = 1;

        playlist.Insert(0, MakeItem("inserted"));

        Assert.Equal(2, playlist.CurrentIndex);
    }

    [Fact]
    public void RemoveAt_RemovesCorrectItem()
    {
        var playlist = new Core.Playlist.Playlist();
        var a = MakeItem("a");
        var b = MakeItem("b");
        var c = MakeItem("c");
        playlist.Add(a);
        playlist.Add(b);
        playlist.Add(c);

        playlist.RemoveAt(1);

        Assert.Equal(2, playlist.Count);
        Assert.Same(a, playlist[0]);
        Assert.Same(c, playlist[1]);
    }

    [Fact]
    public void RemoveAt_AdjustsCurrentIndexWhenRemovedBefore()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));
        playlist.Add(MakeItem("c"));
        playlist.CurrentIndex = 2;

        playlist.RemoveAt(0);

        Assert.Equal(1, playlist.CurrentIndex);
    }

    [Fact]
    public void Move_ReordersItems()
    {
        var playlist = new Core.Playlist.Playlist();
        var a = MakeItem("a");
        var b = MakeItem("b");
        var c = MakeItem("c");
        playlist.Add(a);
        playlist.Add(b);
        playlist.Add(c);

        playlist.Move(0, 2);

        Assert.Same(b, playlist[0]);
        Assert.Same(c, playlist[1]);
        Assert.Same(a, playlist[2]);
    }

    [Fact]
    public void Move_UpdatesCurrentIndexToFollowTrack()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));
        playlist.Add(MakeItem("c"));
        playlist.CurrentIndex = 0;

        playlist.Move(0, 2);

        Assert.Equal(2, playlist.CurrentIndex);
    }

    // ── Sequential navigation ─────────────────────────────────────────

    [Fact]
    public void MoveNext_AdvancesSequentially()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));
        playlist.Add(MakeItem("c"));
        playlist.CurrentIndex = 0;

        var next = playlist.MoveNext();
        Assert.NotNull(next);
        Assert.Equal(1, playlist.CurrentIndex);

        next = playlist.MoveNext();
        Assert.NotNull(next);
        Assert.Equal(2, playlist.CurrentIndex);
    }

    [Fact]
    public void MoveNext_ReturnsNullAtEnd()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));
        playlist.CurrentIndex = 1;

        var next = playlist.MoveNext();
        Assert.Null(next);
        Assert.Equal(1, playlist.CurrentIndex); // Unchanged.
    }

    [Fact]
    public void MovePrevious_GoesBackSequentially()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));
        playlist.Add(MakeItem("c"));
        playlist.CurrentIndex = 2;

        var prev = playlist.MovePrevious();
        Assert.NotNull(prev);
        Assert.Equal(1, playlist.CurrentIndex);
    }

    [Fact]
    public void MovePrevious_ReturnsNullAtStart()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));
        playlist.CurrentIndex = 0;

        var prev = playlist.MovePrevious();
        Assert.Null(prev);
        Assert.Equal(0, playlist.CurrentIndex); // Unchanged.
    }

    // ── ShuffleItems (destructive reorder) ────────────────────────────

    [Fact]
    public void ShuffleItems_KeepsCurrentTrackAtFront()
    {
        var playlist = new Core.Playlist.Playlist();
        var current = MakeItem("current");
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));
        playlist.Add(current);
        playlist.Add(MakeItem("d"));
        playlist.Add(MakeItem("e"));
        playlist.CurrentIndex = 2; // "current"

        playlist.ShuffleItems();

        Assert.Equal(0, playlist.CurrentIndex);
        Assert.Same(current, playlist[0]);
    }

    [Fact]
    public void ShuffleItems_PreservesAllItems()
    {
        var playlist = new Core.Playlist.Playlist();
        var items = Enumerable.Range(0, 20).Select(i => MakeItem($"t{i}")).ToList();
        playlist.AddRange(items);
        playlist.CurrentIndex = 5;

        var idsBefore = items.Select(i => i.Id).OrderBy(id => id).ToList();

        playlist.ShuffleItems();

        var idsAfter = playlist.Select(i => i.Id).OrderBy(id => id).ToList();
        Assert.Equal(idsBefore, idsAfter);
        Assert.Equal(20, playlist.Count);
    }

    [Fact]
    public void ShuffleItems_FiresChangedEvent()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));
        playlist.Add(MakeItem("c"));

        var fired = false;
        playlist.Changed += (_, _) => fired = true;

        playlist.ShuffleItems();
        Assert.True(fired);
    }

    [Fact]
    public void ShuffleItems_DoesNothingForSingleItem()
    {
        var playlist = new Core.Playlist.Playlist();
        var item = MakeItem("only");
        playlist.Add(item);
        playlist.CurrentIndex = 0;

        playlist.ShuffleItems();

        Assert.Single(playlist);
        Assert.Same(item, playlist[0]);
    }

    // ── IndexOf ───────────────────────────────────────────────────────

    [Fact]
    public void IndexOf_ByItem_FindsCorrectIndex()
    {
        var playlist = new Core.Playlist.Playlist();
        var target = MakeItem("target");
        playlist.Add(MakeItem("a"));
        playlist.Add(target);
        playlist.Add(MakeItem("c"));

        Assert.Equal(1, playlist.IndexOf(target));
    }

    [Fact]
    public void IndexOf_ById_FindsCorrectIndex()
    {
        var playlist = new Core.Playlist.Playlist();
        var target = MakeItem("target");
        playlist.Add(MakeItem("a"));
        playlist.Add(target);

        Assert.Equal(1, playlist.IndexOf(target.Id));
    }

    [Fact]
    public void IndexOf_ReturnsNegativeOneForMissing()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));

        Assert.Equal(-1, playlist.IndexOf(Guid.NewGuid()));
        Assert.Equal(-1, playlist.IndexOf(MakeItem("not_in_list")));
    }

    // ── Events ────────────────────────────────────────────────────────

    [Fact]
    public void Changed_FiresOnAdd()
    {
        var playlist = new Core.Playlist.Playlist();
        var fired = false;
        playlist.Changed += (_, _) => fired = true;

        playlist.Add(MakeItem("a"));
        Assert.True(fired);
    }

    [Fact]
    public void Changed_FiresOnRemove()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));

        var fired = false;
        playlist.Changed += (_, _) => fired = true;

        playlist.RemoveAt(0);
        Assert.True(fired);
    }

    [Fact]
    public void Changed_FiresOnClear()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));

        var fired = false;
        playlist.Changed += (_, _) => fired = true;

        playlist.Clear();
        Assert.True(fired);
    }

    [Fact]
    public void CurrentIndexChanged_FiresOnSet()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));

        int? reported = null;
        playlist.CurrentIndexChanged += (_, idx) => reported = idx;

        playlist.CurrentIndex = 1;
        Assert.Equal(1, reported);
    }

    [Fact]
    public void CurrentIndexChanged_DoesNotFireWhenUnchanged()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.CurrentIndex = 0;

        var fired = false;
        playlist.CurrentIndexChanged += (_, _) => fired = true;

        playlist.CurrentIndex = 0;
        Assert.False(fired);
    }

    [Fact]
    public void Enumerable_IteratesAllItems()
    {
        var playlist = new Core.Playlist.Playlist();
        playlist.Add(MakeItem("a"));
        playlist.Add(MakeItem("b"));
        playlist.Add(MakeItem("c"));

        var count = playlist.Count();
        Assert.Equal(3, count);
    }
}
