using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Orpheus.Android;

/// <summary>
/// Converts bool → FontWeight (SemiBold when true, Normal when false).
/// Used to highlight the active tab label.
/// </summary>
public sealed class BoolToFontWeightConverter : IValueConverter
{
    public static readonly BoolToFontWeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.SemiBold : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts bool → IBrush.
/// true  → AccentColor from application resources (falls back to #D4A843)
/// false → TextMuted from application resources (falls back to #9A9488)
/// </summary>
public sealed class BoolToAccentBrushConverter : IValueConverter
{
    public static readonly BoolToAccentBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var app = Application.Current;
        var variant = app?.ActualThemeVariant;

        if (value is true)
        {
            if (app is not null &&
                app.Resources.TryGetResource("AccentColor", variant, out var raw) &&
                raw is Color accent)
                return new SolidColorBrush(accent);
            return new SolidColorBrush(Color.Parse("#D4A843"));
        }
        else
        {
            if (app is not null &&
                app.Resources.TryGetResource("TextMuted", variant, out var raw) &&
                raw is Color muted)
                return new SolidColorBrush(muted);
            return new SolidColorBrush(Color.Parse("#9A9488"));
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts int → bool (true when count > 0). Used to show/hide lists.
/// </summary>
public sealed class CountToBoolConverter : IValueConverter
{
    public static readonly CountToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts string → bool (true when non-null and non-empty).
/// Used to conditionally show secondary text rows.
/// </summary>
public sealed class StringToBoolConverter : IValueConverter
{
    public static readonly StringToBoolConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrEmpty(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts bool → rescan button label ("Rescan Library" / "Scanning…").
/// </summary>
public sealed class BoolToRescanLabelConverter : IValueConverter
{
    public static readonly BoolToRescanLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Scanning…" : "Rescan Library";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverts a bool. Used to show/hide controls that are visible only at the root library level.
/// </summary>
public sealed class BoolInverseConverter : IValueConverter
{
    public static readonly BoolInverseConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>
/// Converts bool → chevron character (▾ when expanded, ▸ when collapsed).
/// </summary>
public sealed class BoolToChevronConverter : IValueConverter
{
    public static readonly BoolToChevronConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▾" : "▸";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts bool → IBrush for queue row backgrounds.
/// true  → RowSelected (playing item highlight)
/// false → Transparent
/// </summary>
public sealed class BoolToRowBrushConverter : IValueConverter
{
    public static readonly BoolToRowBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true) return new SolidColorBrush(Colors.Transparent);

        var app     = Application.Current;
        var variant = app?.ActualThemeVariant;
        if (app is not null &&
            app.Resources.TryGetResource("RowSelected", variant, out var raw) &&
            raw is Color c)
            return new SolidColorBrush(c);

        return new SolidColorBrush(Color.Parse("#3A3122"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Splits the "m:ss / m:ss" NowPlayingTime string and returns either the position
/// or duration half, so both can be shown as separate labels flanking the seek bar.
/// </summary>
public sealed class TimeStringSplitConverter : IValueConverter
{
    /// <summary>Returns the position portion (before " / ").</summary>
    public static readonly TimeStringSplitConverter PositionInstance = new(returnPosition: true);
    /// <summary>Returns the duration portion (after " / ").</summary>
    public static readonly TimeStringSplitConverter DurationInstance = new(returnPosition: false);

    private readonly bool _returnPosition;
    private TimeStringSplitConverter(bool returnPosition) => _returnPosition = returnPosition;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        var sep = s.IndexOf(" / ", StringComparison.Ordinal);
        if (sep < 0) return s;
        return _returnPosition ? s[..sep] : s[(sep + 3)..];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
