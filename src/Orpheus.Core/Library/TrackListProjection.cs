using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Orpheus.Core.Library;

public enum TrackListSortField
{
    Title,
    Artist,
    Album,
    FileName,
    TrackNumber,
    Year,
    Duration,
    DateAdded,
    Bitrate,
}

public sealed record TrackListProjectionOptions(
    TrackListSortField SortField = TrackListSortField.Title,
    bool SortAscending = true,
    bool EnableSort = true,
    bool SuppressSortAndFilter = false,
    bool HideMissingTitle = false,
    bool HideMissingArtist = false,
    bool HideMissingAlbum = false,
    bool HideMissingGenre = false,
    bool HideMissingTrackNumber = false);

public sealed record TrackListProjectionSelectors<T>(
    Func<T, string?> Title,
    Func<T, string?> Artist,
    Func<T, string?> Album,
    Func<T, string?> FileName,
    Func<T, string?> TrackNumber,
    Func<T, string?> Year,
    Func<T, string?> Genre,
    Func<T, string?> Bitrate,
    Func<T, string?> Duration,
    Func<T, long?>? DateAddedTicks = null);

public static class TrackListProjection
{
    public static IReadOnlyList<T> Apply<T>(
        IEnumerable<T> source,
        TrackListProjectionOptions options,
        TrackListProjectionSelectors<T> selectors)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selectors);

        IEnumerable<T> query = source;

        if (!options.SuppressSortAndFilter)
        {
            if (options.HideMissingTitle)
                query = query.Where(track => !string.IsNullOrWhiteSpace(selectors.Title(track)));
            if (options.HideMissingArtist)
                query = query.Where(track => !string.IsNullOrWhiteSpace(selectors.Artist(track)));
            if (options.HideMissingAlbum)
                query = query.Where(track => !string.IsNullOrWhiteSpace(selectors.Album(track)));
            if (options.HideMissingGenre)
                query = query.Where(track => !string.IsNullOrWhiteSpace(selectors.Genre(track)));
            if (options.HideMissingTrackNumber)
                query = query.Where(track => int.TryParse(selectors.TrackNumber(track), out var number) && number > 0);

            if (options.EnableSort)
                query = ApplySort(query, options, selectors);
        }

        return query.ToList();
    }

    private static IEnumerable<T> ApplySort<T>(
        IEnumerable<T> tracks,
        TrackListProjectionOptions options,
        TrackListProjectionSelectors<T> selectors)
    {
        return options.SortField switch
        {
            TrackListSortField.Title => OrderByString(tracks, selectors.Title, options.SortAscending),
            TrackListSortField.Artist => OrderByString(tracks, selectors.Artist, options.SortAscending),
            TrackListSortField.Album => OrderByString(tracks, selectors.Album, options.SortAscending),
            TrackListSortField.FileName => OrderByString(tracks, selectors.FileName, options.SortAscending),
            TrackListSortField.TrackNumber => OrderByNumber(tracks, track => ParseSortableUInt(selectors.TrackNumber(track)), options.SortAscending),
            TrackListSortField.Year => OrderByNumber(tracks, track => ParseSortableUInt(selectors.Year(track)), options.SortAscending),
            TrackListSortField.Duration => OrderByNumber(tracks, track => ParseSortableDuration(selectors.Duration(track)), options.SortAscending),
            TrackListSortField.DateAdded when selectors.DateAddedTicks is not null => OrderByNumber(tracks, track => selectors.DateAddedTicks(track) ?? long.MaxValue, options.SortAscending),
            TrackListSortField.DateAdded => tracks,
            TrackListSortField.Bitrate => OrderByNumber(tracks, track => ParseSortableInt(selectors.Bitrate(track)), options.SortAscending),
            _ => OrderByString(tracks, selectors.Title, options.SortAscending),
        };
    }

    private static IEnumerable<T> OrderByString<T>(
        IEnumerable<T> tracks,
        Func<T, string?> selector,
        bool ascending)
    {
        return ascending
            ? tracks.OrderBy(track => selector(track) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            : tracks.OrderByDescending(track => selector(track) ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<T> OrderByNumber<T, TValue>(
        IEnumerable<T> tracks,
        Func<T, TValue> selector,
        bool ascending) where TValue : IComparable<TValue>
    {
        return ascending
            ? tracks.OrderBy(selector)
            : tracks.OrderByDescending(selector);
    }

    private static uint ParseSortableUInt(string? value) =>
        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : uint.MaxValue;

    private static int ParseSortableInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return int.MaxValue;

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : int.MaxValue;
    }

    private static long ParseSortableDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return long.MaxValue;

        if (TimeSpan.TryParseExact(value, @"h\:mm\:ss", CultureInfo.InvariantCulture, out var withHours))
            return withHours.Ticks;

        if (TimeSpan.TryParseExact(value, @"m\:ss", CultureInfo.InvariantCulture, out var shortForm))
            return shortForm.Ticks;

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration)
            ? duration.Ticks
            : long.MaxValue;
    }
}
