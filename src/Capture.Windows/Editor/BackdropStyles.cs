using Capture.Core;

namespace Capture.Windows.Editor;

/// <summary>Backdrop looks the user saved, shared by every editor window. The app persists them when they change.</summary>
internal static class BackdropStyles
{
    internal const int Limit = 8;
    private static readonly List<Backdrop> styles = [];
    internal static IReadOnlyList<Backdrop> Styles => styles;
    internal static event Action? Changed;

    internal static void Load(IEnumerable<Backdrop>? stored)
    {
        styles.Clear();
        foreach (var style in stored ?? [])
        {
            var kept = style with { Enabled = true };
            if (kept.IsValid && styles.Count < Limit && !styles.Contains(kept)) styles.Add(kept);
        }
    }

    internal static void Add(Backdrop style)
    {
        var kept = style with { Enabled = true };
        styles.Remove(kept);
        styles.Insert(0, kept);
        if (styles.Count > Limit) styles.RemoveAt(styles.Count - 1);
        Changed?.Invoke();
    }

    internal static void Remove(Backdrop style) { if (styles.Remove(style)) Changed?.Invoke(); }
}
