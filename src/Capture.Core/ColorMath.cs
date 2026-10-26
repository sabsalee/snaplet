using System.Globalization;

namespace Capture.Core;

/// <summary>Colour formats and contrast metrics for the eyedropper. Alpha is ignored: sampled screen pixels are opaque.</summary>
public static class ColorMath
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Hsl(ColorRgba c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > .5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return string.Format(Invariant, "hsl({0:0}, {1:0}%, {2:0}%)", h, s * 100, l * 100);
    }

    /// <summary>OKLCH via Björn Ottosson's OKLab, formatted like CSS: <c>oklch(62.8% 0.258 29.2)</c>.</summary>
    public static (double L, double C, double H) Oklch(ColorRgba c)
    {
        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        double L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        double A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        double B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        double C = Math.Sqrt(A * A + B * B), H = C < 1e-4 ? 0 : (Math.Atan2(B, A) * 180 / Math.PI + 360) % 360;
        return (L, C, H);
    }
    public static string OklchText(ColorRgba c)
    {
        var (l, ch, h) = Oklch(c);
        return string.Format(Invariant, "oklch({0:0.#}% {1:0.###} {2:0.#})", l * 100, ch, h);
    }

    /// <summary>WCAG 2.x relative luminance and contrast ratio (1–21).</summary>
    public static double RelativeLuminance(ColorRgba c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    public static double WcagRatio(ColorRgba a, ColorRgba b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
    /// <summary>WCAG 2 level for normal text: AAA ≥ 7, AA ≥ 4.5; large text AA ≥ 3.</summary>
    public static string WcagLevel(double ratio) => ratio >= 7 ? "AAA" : ratio >= 4.5 ? "AA" : ratio >= 3 ? "AA 큰 글자" : "미달";

    /// <summary>
    /// APCA lightness contrast Lc (APCA-W3 0.0.98G constants). Positive for dark text on light background, negative for light on dark.
    /// Order matters: the first argument is text, the second background.
    /// </summary>
    public static double ApcaLc(ColorRgba text, ColorRgba background)
    {
        static double Y(ColorRgba c)
        {
            double y = 0.2126729 * Math.Pow(c.R / 255.0, 2.4) + 0.7151522 * Math.Pow(c.G / 255.0, 2.4) + 0.0721750 * Math.Pow(c.B / 255.0, 2.4);
            return y < 0.022 ? y + Math.Pow(0.022 - y, 1.414) : y;
        }
        double yt = Y(text), yb = Y(background);
        if (Math.Abs(yb - yt) < 0.0005) return 0;
        double output;
        if (yb > yt)
        {
            double sapc = (Math.Pow(yb, 0.56) - Math.Pow(yt, 0.57)) * 1.14;
            output = sapc < 0.1 ? 0 : sapc - 0.027;
        }
        else
        {
            double sapc = (Math.Pow(yb, 0.65) - Math.Pow(yt, 0.62)) * 1.14;
            output = sapc > -0.1 ? 0 : sapc + 0.027;
        }
        return output * 100;
    }

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
