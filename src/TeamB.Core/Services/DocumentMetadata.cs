using System.Globalization;
using System.Text.RegularExpressions;

namespace TeamB.Core.Services;

/// <summary>
/// Derives a friendly title and ISO meeting date from a source file name such as
/// "04 15 26 -- Business Meeting.pdf" or "06 17 26 -- Business Meeting_&lt;guid&gt;.pdf".
/// </summary>
public static partial class DocumentMetadata
{
    public sealed record Info(string Title, string? MeetingDate);

    public static Info FromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        string? isoDate = null;
        var dateLabel = string.Empty;
        var afterDate = name;
        var match = LeadingDateRegex().Match(name);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, out var month)
            && int.TryParse(match.Groups[2].Value, out var day)
            && int.TryParse(match.Groups[3].Value, out var yearValue))
        {
            // Accept 2- or 4-digit years (e.g. "26" -> 2026, "2026" -> 2026).
            var year = yearValue >= 100 ? yearValue : 2000 + yearValue;
            if (TryBuildDate(year, month, day, out var date))
            {
                isoDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                dateLabel = date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
                afterDate = name[match.Length..];
            }
        }

        // Descriptor = everything after the leading date; drop any GUID suffix and normalize separators.
        var descriptor = GuidSuffixRegex().Replace(afterDate, string.Empty).Replace('_', ' ');
        descriptor = descriptor.Trim().Trim('-').Trim();      // strip a leading "--" separator
        descriptor = SeparatorRegex().Replace(descriptor, " \u2014 ");   // " -- " -> " — "
        descriptor = WhitespaceRegex().Replace(descriptor, " ").Trim();
        if (descriptor.Length == 0) descriptor = "Meeting";

        var title = dateLabel.Length > 0 ? $"{descriptor} \u2014 {dateLabel}" : descriptor;
        return new Info(title, isoDate);
    }

    private static bool TryBuildDate(int year, int month, int day, out DateOnly date)
    {
        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            date = default;
            return false;
        }
    }

    [GeneratedRegex(@"^\s*(\d{1,2})\s+(\d{1,2})\s+(\d{2,4})")]
    private static partial Regex LeadingDateRegex();

    [GeneratedRegex(@"[_\s-]*[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidSuffixRegex();

    [GeneratedRegex(@"\s*--\s*")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
