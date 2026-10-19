using System.Windows.Media;

namespace WindowsCluster.App.Theming;

internal static class ColorMath
{
    /// <summary>HSL (hue 0–360, saturation and lightness 0–1) to an opaque color.</summary>
    public static Color FromHsl(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var h = (hue % 360) / 60;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = h switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = lightness - c / 2;
        return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    /// <summary>Relative luminance (0–1) as defined by WCAG.</summary>
    public static double Luminance(Color color)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}
