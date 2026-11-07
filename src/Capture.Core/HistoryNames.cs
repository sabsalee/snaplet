using System.Globalization;

namespace Capture.Core;

/// <summary>File names and date headings of the capture history, kept free of UI so they can be tested anywhere.</summary>
public static class HistoryNames
{
    private const string TimeFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>A sortable name: newest last in ordinal order, and unique even within one millisecond.</summary>
    public static string Create(DateTime utc, string unique) => utc.ToString(TimeFormat, CultureInfo.InvariantCulture) + "-" + unique + ".png";

    /// <summary>Reads the capture time back from a name made by <see cref="Create"/>. Any other file yields false.</summary>
    public static bool TryParseUtc(string fileName, out DateTime utc)
    {
        utc = default;
        return fileName.Length > TimeFormat.Length &&
            DateTime.TryParseExact(fileName.AsSpan(0, TimeFormat.Length), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }

    /// <summary>Heading for a capture: today, yesterday, or its date. Compares calendar days, not 24-hour spans.</summary>
    public static string Heading(DateTime when, DateTime now)
    {
        int days = (now.Date - when.Date).Days;
        if (days == 0) return "오늘";
        if (days == 1) return "어제";
        return when.Year == now.Year ? when.ToString("M월 d일", CultureInfo.InvariantCulture) : when.ToString("yyyy년 M월 d일", CultureInfo.InvariantCulture);
    }
}
