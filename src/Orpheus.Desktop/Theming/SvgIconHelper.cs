using Avalonia.Media;
using Avalonia.Svg.Skia;

namespace Orpheus.Desktop.Theming;

/// <summary>
/// Loads SVG icons from avares:// URIs and produces <see cref="SvgImage"/>
/// instances tinted to a specific color.  The fill/stroke override is
/// applied via the <c>Css</c> property on <see cref="SvgImage"/> using
/// <c>!important</c> to override inline styles in the SVG files.
/// </summary>
public static class SvgIconHelper
{
    /// <summary>
    /// Creates a CSS string that forces all fills and strokes to the given color.
    /// </summary>
    public static string ColorToCss(Color color) =>
        @$"* {{
        fill: #{color.R:X2}{color.G:X2}{color.B:X2} !important;
        stroke: #{color.R:X2}{color.G:X2}{color.B:X2} !important;
        bordercolor: #{color.R:X2}{color.G:X2}{color.B:X2} !important;
        color: #{color.R:X2}{color.G:X2}{color.B:X2} !important;
        }}";

    /// <summary>
    /// Loads an SVG from an avares:// path and returns an <see cref="SvgImage"/>
    /// tinted to the specified color.
    /// </summary>
    public static SvgImage Load(string avaresPath, Color color)
    {
        var source = SvgSource.Load(avaresPath, baseUri: null);
        return new SvgImage
        {
            Source = source,
            Css = ColorToCss(color),
        };
    }

    /// <summary>
    /// Loads an SVG from an avares:// path and returns an <see cref="SvgImage"/>
    /// tinted using a CSS string (e.g. from <c>IconCss</c> theme resource).
    /// </summary>
    public static SvgImage Load(string avaresPath, string css)
    {
        var source = SvgSource.Load(avaresPath, baseUri: null);
        return new SvgImage
        {
            Source = source,
            Css = css,
        };
    }
}
