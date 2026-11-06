using System.Windows.Media;

namespace Capture.Windows.Editor;

/// <summary>Fonts for the text tool. A missing font falls through a fixed list, so Korean text always renders.</summary>
internal static class TextFonts
{
    internal const string Preferred = "Noto Sans KR";
    private const string Fallback = "Malgun Gothic, Segoe UI";
    private static readonly Dictionary<string, FontFamily> cache = new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<string>? installed;
    private static string defaultName = Preferred;

    /// <summary>The font of text that has not picked its own; blank means <see cref="Preferred"/>.</summary>
    internal static string DefaultName
    {
        get => defaultName;
        set => defaultName = string.IsNullOrWhiteSpace(value) ? Preferred : value.Trim();
    }

    internal static FontFamily For(string? name)
    {
        var key = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        if (!cache.TryGetValue(key, out var family))
        {
            var chain = key.Equals(Preferred, StringComparison.OrdinalIgnoreCase) ? $"{key}, {Fallback}" : $"{key}, {Preferred}, {Fallback}";
            family = new FontFamily(chain);
            cache[key] = family;
        }
        return family;
    }

    internal static IReadOnlyList<string> Installed => installed ??= Fonts.SystemFontFamilies
        .Select(f => f.Source).Where(n => !n.StartsWith('@')).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();

    internal static bool IsInstalled(string name) => Installed.Contains(name, StringComparer.OrdinalIgnoreCase);
}
