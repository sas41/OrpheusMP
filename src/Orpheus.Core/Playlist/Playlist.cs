using System.Collections;

namespace Orpheus.Core.Playlist;

/// <summary>
/// An ordered collection of playlist items with sequential navigation.
/// This is a data structure — it does not know about shuffle play or repeat.
/// Shuffle play is handled by <see cref="Playback.PlayerController"/>.
/// </summary>
public sealed class Playlist : IReadOnlyList<PlaylistItem>
{
    private readonly List<PlaylistItem> _items = [];
    private readonly Random _rng = new();
    private int _currentIndex = -1;

    /// <summary>
    /// Optional name for this playlist.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Number of items in the playlist.
    /// </summary>
    public int Count => _items.Count;

    /// <summary>
    /// The index of the currently selected track, or -1 if none.
    /// </summary>
    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            if (value < -1 || value >= _items.Count)
                throw new ArgumentOutOfRangeException(nameof(value));
            var old = _currentIndex;
            _currentIndex = value;
            if (old != value)
                CurrentIndexChanged?.Invoke(this, _currentIndex);
        }
    }

    /// <summary>
    /// The currently selected item, or null if none.
    /// </summary>
    public PlaylistItem? CurrentItem =>
        _currentIndex >= 0 && _currentIndex < _items.Count
            ? _items[_currentIndex]
            : null;

    public PlaylistItem this[int index] => _items[index];

    /// <summary>
    /// Fired when the playlist contents change (add, remove, clear, reorder, shuffle).
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Fired when the current track index changes.
    /// </summary>
    public event EventHandler<int>? CurrentIndexChanged;

    /// <summary>
    /// Add an item to the end of the playlist.
    /// </summary>
    public void Add(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Add multiple items to the end of the playlist.
    /// </summary>
    public void AddRange(IEnumerable<PlaylistItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.AddRange(items);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Insert an item at the specified index.
    /// </summary>
    public void Insert(int index, PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Insert(index, item);

        if (_currentIndex >= index) _currentIndex++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Insert multiple items starting at the specified index.
    /// </summary>
    public void InsertRange(int index, IReadOnlyList<PlaylistItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return;

        _items.InsertRange(index, items);

        if (_currentIndex >= index) _currentIndex += items.Count;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Remove the item at the specified index.
    /// </summary>
    public void RemoveAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _items.Count);

        _items.RemoveAt(index);

        if (_currentIndex == index)
        {
            // Reset to -1 first so that the caller assigning the new index via
            // the CurrentIndex setter sees an actual change and fires CurrentIndexChanged.
            _currentIndex = -1;
        }
        else if (_currentIndex > index)
            _currentIndex--;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Remove all items.
    /// </summary>
    public void Clear()
    {
        _items.Clear();
        _currentIndex = -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Move an item from one position to another.
    /// </summary>
    public void Move(int fromIndex, int toIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromIndex, _items.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(toIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(toIndex, _items.Count);

        if (fromIndex == toIndex) return;

        var item = _items[fromIndex];
        _items.RemoveAt(fromIndex);
        _items.Insert(toIndex, item);

        // Update current index to follow the current track.
        if (_currentIndex == fromIndex)
            _currentIndex = toIndex;
        else if (fromIndex < _currentIndex && toIndex >= _currentIndex)
            _currentIndex--;
        else if (fromIndex > _currentIndex && toIndex <= _currentIndex)
            _currentIndex++;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Advance to the next item sequentially.
    /// Returns the next item, or null if at the end.
    /// This is raw sequential navigation — repeat and shuffle play
    /// logic is handled by <see cref="Playback.PlayerController"/>.
    /// </summary>
    public PlaylistItem? MoveNext()
    {
        if (_items.Count == 0) return null;

        var next = _currentIndex + 1;
        if (next >= _items.Count) return null;

        _currentIndex = next;
        CurrentIndexChanged?.Invoke(this, _currentIndex);
        return CurrentItem;
    }

    /// <summary>
    /// Go back to the previous item sequentially.
    /// Returns the previous item, or null if at the start.
    /// </summary>
    public PlaylistItem? MovePrevious()
    {
        if (_items.Count == 0) return null;

        var prev = _currentIndex - 1;
        if (prev < 0) return null;

        _currentIndex = prev;
        CurrentIndexChanged?.Invoke(this, _currentIndex);
        return CurrentItem;
    }

    /// <summary>
    /// Shuffle Playlist: physically reorders all items in-place using
    /// Fisher-Yates. This is a destructive operation — the original
    /// order is lost. The current track (if any) is moved to position 0.
    /// </summary>
    public void ShuffleItems()
    {
        if (_items.Count <= 1) return;

        // If there's a current track, swap it to the front first.
        if (_currentIndex > 0)
        {
            (_items[0], _items[_currentIndex]) = (_items[_currentIndex], _items[0]);
            _currentIndex = 0;
        }

        // Fisher-Yates from index 1 onward (keep current track at 0).
        var startIndex = _currentIndex >= 0 ? 1 : 0;
        for (var i = _items.Count - 1; i > startIndex; i--)
        {
            var j = _rng.Next(startIndex, i + 1);
            (_items[i], _items[j]) = (_items[j], _items[i]);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Get the index of a playlist item by its ID.
    /// Returns -1 if not found.
    /// </summary>
    public int IndexOf(Guid itemId)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Id == itemId) return i;
        }
        return -1;
    }

    /// <summary>
    /// Get the index of a playlist item by reference.
    /// Returns -1 if not found.
    /// </summary>
    public int IndexOf(PlaylistItem item)
    {
        return _items.IndexOf(item);
    }

    public IEnumerator<PlaylistItem> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
