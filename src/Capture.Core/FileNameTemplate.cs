namespace Capture.Core;

/// <summary>Turns a user template such as <c>Snaplet-%Y%m%d-%H%M%S</c> into a safe file name stem.</summary>
public static class FileNameTemplate
{
    public const string Default = "Snaplet-%Y%m%d-%H%M%S-%f-%r";
    private const int MaxLength = 120;
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>%Y %m %d %H %M %S date and time, %f milliseconds, %r four random hex digits, %% a percent sign. Other text is kept.</summary>
    public static string Render(string? template, DateTime time, string random)
    {
        var name = Expand(string.IsNullOrWhiteSpace(template) ? Default : template, time, random);
        name = Clean(name);
        return name.Length == 0 ? Clean(Expand(Default, time, random)) : name;
    }

    private static string Expand(string template, DateTime time, string random)
    {
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '%' || i == template.Length - 1) { result.Append(template[i]); continue; }
            i++;
            switch (template[i])
            {
                case 'Y': result.Append(time.Year.ToString("0000")); break;
                case 'm': result.Append(time.Month.ToString("00")); break;
                case 'd': result.Append(time.Day.ToString("00")); break;
                case 'H': result.Append(time.Hour.ToString("00")); break;
                case 'M': result.Append(time.Minute.ToString("00")); break;
                case 'S': result.Append(time.Second.ToString("00")); break;
                case 'f': result.Append(time.Millisecond.ToString("000")); break;
                case 'r': result.Append(random); break;
                case '%': result.Append('%'); break;
                default: result.Append('%').Append(template[i]); break;
            }
        }
        return result.ToString();
    }

    private static string Clean(string name)
    {
        // Windows rules hold on every platform: these names end up on NTFS.
        const string invalid = "<>:\"/\\|?*";
        var cleaned = new string(name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (cleaned.Length > MaxLength) cleaned = cleaned[..MaxLength].TrimEnd('.', ' ');
        var stem = cleaned.Split('.')[0];
        return Reserved.Contains(stem) ? "_" + cleaned : cleaned;
    }
}
