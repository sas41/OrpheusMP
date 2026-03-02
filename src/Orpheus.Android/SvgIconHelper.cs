using Avalonia.Media;
using Avalonia.Svg.Skia;

namespace Orpheus.Android;

public static class SvgIconHelper
{
    public static string ColorToCss(Color color) =>
        @$"* {{
        fill: #{color.R:X2}{color.G:X2}{color.B:X2} !important;
        stroke: #{color.R:X2}{color.G:X2}{color.B:X2} !important;
        bordercolor: #{color.R:X2}{color.G:X2}{color.B:X2} !important;
        color: #{color.R:X2}{color.G:X2}{color.B:X2} !important;
        }}";

    public static SvgImage Load(string avaresPath, Color color)
    {
        var source = SvgSource.Load(avaresPath, baseUri: null);
        return new SvgImage
        {
            Source = source,
            Css = ColorToCss(color),
        };
    }

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
