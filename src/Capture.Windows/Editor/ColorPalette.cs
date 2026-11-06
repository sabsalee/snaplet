using System.Windows.Media;
using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>The user's saved colours, shared by every editor window. The app persists them when they change.</summary>
internal static class ColorPalette
{
    internal const int Limit = 12;
    private static readonly List<ColorRgba> colors = [];
    internal static IReadOnlyList<ColorRgba> Colors => colors;
    internal static event Action? Changed;

    internal static void Load(IEnumerable<string>? stored)
    {
        colors.Clear();
        foreach (var value in stored ?? [])
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(value);
                var color = new ColorRgba(c.R, c.G, c.B, c.A);
                if (colors.Count < Limit && !colors.Contains(color)) colors.Add(color);
            }
            catch (FormatException) { }
            catch (NullReferenceException) { }
        }
    }

    internal static IReadOnlyList<string> ToStored() => colors.Select(c => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}").ToArray();

    internal static void Add(ColorRgba color)
    {
        colors.Remove(color);
        colors.Insert(0, color);
        if (colors.Count > Limit) colors.RemoveAt(colors.Count - 1);
        Changed?.Invoke();
    }

    internal static void Remove(ColorRgba color) { if (colors.Remove(color)) Changed?.Invoke(); }
}
