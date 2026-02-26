using System.Collections;
using System.Collections.Specialized;

namespace Orpheus.Core.Playlist;

/// <summary>
/// An ordered collection of playlist items with support for shuffle,
/// repeat, and navigation.
/// </summary>
public sealed class Playlist : IReadOnlyList<PlaylistItem>
{
    private readonly List<PlaylistItem> _items = [];
    private readonly List<int> _shuffleOrder = [];
    private readonly Random _rng = new();
    private int _currentIndex = -1;
    private bool _shuffle;

    /// <summary>
    /// Optional name for this playlist.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Number of items in the playlist.
    /// </summary>
    public int Count => _items.Count;

    /// <summary>
    /// Current repeat mode.
    /// </summary>
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;

    /// <summary>
    /// Whether shuffle is enabled.
    /// </summary>
    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            if (_shuffle) RebuildShuffleOrder();
        }
    }

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
            _currentIndex = value;
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
    /// Fired when the playlist contents change (add, remove, clear, reorder).
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

        if (_shuffle)
        {
            // Insert the new index at a random position in shuffle order.
            var newIndex = _items.Count - 1;
            _shuffleOrder.Insert(_rng.Next(_shuffleOrder.Count + 1), newIndex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Add multiple items to the end of the playlist.
    /// </summary>
    public void AddRange(IEnumerable<PlaylistItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var startIndex = _items.Count;
        _items.AddRange(items);

        if (_shuffle)
        {
            for (var i = startIndex; i < _items.Count; i++)
                _shuffleOrder.Insert(_rng.Next(_shuffleOrder.Count + 1), i);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Insert an item at the specified index.
    /// </summary>
    public void Insert(int index, PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Insert(index, item);

        if (_shuffle) RebuildShuffleOrder();
        if (_currentIndex >= index) _currentIndex++;

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

        if (_shuffle) RebuildShuffleOrder();

        if (_currentIndex == index)
            _currentIndex = Math.Min(_currentIndex, _items.Count - 1);
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
        _shuffleOrder.Clear();
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

        if (_shuffle) RebuildShuffleOrder();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Advance to the next track. Returns the next item, or null if at the end
    /// (and repeat is None).
    /// </summary>
    public PlaylistItem? MoveNext()
    {
        if (_items.Count == 0) return null;

        if (RepeatMode == RepeatMode.One)
            return CurrentItem;

        var nextIndex = GetNextIndex();
        if (nextIndex is null) return null;

        _currentIndex = nextIndex.Value;
        CurrentIndexChanged?.Invoke(this, _currentIndex);
        return CurrentItem;
    }

    /// <summary>
    /// Go back to the previous track. Returns the previous item, or null.
    /// </summary>
    public PlaylistItem? MovePrevious()
    {
        if (_items.Count == 0) return null;

        if (RepeatMode == RepeatMode.One)
            return CurrentItem;

        var prevIndex = GetPreviousIndex();
        if (prevIndex is null) return null;

        _currentIndex = prevIndex.Value;
        CurrentIndexChanged?.Invoke(this, _currentIndex);
        return CurrentItem;
    }

    private int? GetNextIndex()
    {
        if (_items.Count == 0) return null;

        if (_shuffle)
        {
            var currentShufflePos = _shuffleOrder.IndexOf(_currentIndex);
            var nextShufflePos = currentShufflePos + 1;

            if (nextShufflePos >= _shuffleOrder.Count)
            {
                if (RepeatMode == RepeatMode.All)
                {
                    RebuildShuffleOrder();
                    return _shuffleOrder[0];
                }
                return null;
            }

            return _shuffleOrder[nextShufflePos];
        }

        var next = _currentIndex + 1;
        if (next >= _items.Count)
        {
            return RepeatMode == RepeatMode.All ? 0 : null;
        }

        return next;
    }

    private int? GetPreviousIndex()
    {
        if (_items.Count == 0) return null;

        if (_shuffle)
        {
            var currentShufflePos = _shuffleOrder.IndexOf(_currentIndex);
            var prevShufflePos = currentShufflePos - 1;

            if (prevShufflePos < 0)
            {
                return RepeatMode == RepeatMode.All ? _shuffleOrder[^1] : null;
            }

            return _shuffleOrder[prevShufflePos];
        }

        var prev = _currentIndex - 1;
        if (prev < 0)
        {
            return RepeatMode == RepeatMode.All ? _items.Count - 1 : null;
        }

        return prev;
    }

    private void RebuildShuffleOrder()
    {
        _shuffleOrder.Clear();
        for (var i = 0; i < _items.Count; i++)
            _shuffleOrder.Add(i);

        // Fisher-Yates shuffle, but keep the current track at position 0.
        for (var i = _shuffleOrder.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (_shuffleOrder[i], _shuffleOrder[j]) = (_shuffleOrder[j], _shuffleOrder[i]);
        }

        // Move current index to front of shuffle order so it doesn't replay immediately.
        if (_currentIndex >= 0 && _currentIndex < _items.Count)
        {
            _shuffleOrder.Remove(_currentIndex);
            _shuffleOrder.Insert(0, _currentIndex);
        }
    }

    public IEnumerator<PlaylistItem> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
