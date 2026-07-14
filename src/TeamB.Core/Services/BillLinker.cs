using System.Globalization;
using System.Text.RegularExpressions;
using TeamB.Core.Models;

namespace TeamB.Core.Services;

/// <summary>
/// Post-processes a chat answer and turns congressional bill references into links to congress.gov.
/// It recognizes both abbreviated forms ("S. 3018", "H.R. 1234") and spelled-out forms
/// ("Senate Bill 3018", "House Resolution 260"), including a single prefix governing a comma/"and"
/// separated run of bare numbers ("Senate Bills 2722, 2222, 3496, and 2236").
///
/// Two guardrails keep this from linking "willy-nilly":
///   1. Grounding — a reference is only linked if the SAME normalized bill id appears in the
///      retrieved source passages the answer was grounded on (surface form can differ, e.g. the
///      source says "Senate Bill 2252" and the answer says "S. 2252" — both normalize to
///      senate-bill:2252 and match).
///   2. Congress resolution — the URL is built from the congress derived from the source meeting's
///      date, so the link points at the correct two-year congress.
/// </summary>
public interface IBillLinker
{
    /// <summary>Returns the answer with grounded bill references rewritten as congress.gov anchors.</summary>
    string AddLinks(string answer, IReadOnlyList<RetrievedChunk> sources);
}

public sealed partial class BillLinker : IBillLinker
{
    public string AddLinks(string answer, IReadOnlyList<RetrievedChunk> sources)
    {
        if (string.IsNullOrWhiteSpace(answer) || sources.Count == 0) return answer;

        // Congress to use for a source whose date can't be parsed (e.g. odd file name).
        var fallbackCongress = FallbackCongress(sources);

        // Build the grounding map: normalized bill id -> congress, from the source passages only.
        var grounded = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source.Content)) continue;
            var congress = CongressFor(source.MeetingDate) ?? fallbackCongress;
            foreach (var id in EnumerateIds(source.Content))
                grounded.TryAdd(id, congress);
        }

        if (grounded.Count == 0) return answer;

        // Chamber+number -> unique grounded id, used to recover mislabeled references (e.g. the model
        // writes "H.R. 260" but the minutes say "House Resolution 260"). Null means ambiguous.
        var byChamberNumber = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in grounded.Keys)
        {
            var key = ChamberNumberKey(id);
            if (byChamberNumber.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing, id, StringComparison.OrdinalIgnoreCase))
                    byChamberNumber[key] = null; // more than one bill of this chamber+number
            }
            else
            {
                byChamberNumber[key] = id;
            }
        }

        // Resolves a reference to (congress, slug), preferring the exact typed form and falling back
        // to the unique grounded bill of the same chamber+number. Returns null if not grounded.
        (int Congress, string Slug)? Resolve(string slug, string number)
        {
            if (grounded.TryGetValue($"{slug}:{number}", out var exact))
                return (exact, slug);

            if (byChamberNumber.TryGetValue($"{ChamberOf(slug)}:{number}", out var alt) && alt is not null)
                return (grounded[alt], alt[..alt.IndexOf(':')]);

            return null;
        }

        // Rewrite only the references in the answer that resolve to a grounded bill.
        return BillRegex().Replace(answer, match =>
        {
            var slug = SlugForType(match.Groups["type"].Value);
            if (slug is null) return match.Value;

            var numsText = match.Groups["nums"].Value;
            var numbers = NumberRegex().Matches(numsText);

            // Single bill: link the whole reference text (e.g. "Senate Bill 2252" or "S. 3018").
            if (numbers.Count == 1)
            {
                var number = numbers[0].Value;
                var r = Resolve(slug, number);
                return r is null ? match.Value : Anchor(Url(r.Value.Congress, r.Value.Slug, number), match.Value);
            }

            // Run of bills under one prefix: link each grounded number, keep the prefix as plain text.
            var prefix = match.Value[..(match.Groups["nums"].Index - match.Index)];
            var linkedNums = NumberRegex().Replace(numsText, nm =>
            {
                var r = Resolve(slug, nm.Value);
                return r is null ? nm.Value : Anchor(Url(r.Value.Congress, r.Value.Slug, nm.Value), nm.Value);
            });
            return prefix + linkedNums;
        });
    }

    /// <summary>Builds a "chamber:number" key from a "slug:number" id (chamber = senate|house).</summary>
    private static string ChamberNumberKey(string id)
    {
        var sep = id.IndexOf(':');
        return $"{ChamberOf(id[..sep])}:{id[(sep + 1)..]}";
    }

    /// <summary>
    /// Buckets a slug by chamber for the chamber+number fallback. Nominations get their own bucket so
    /// they never satisfy a bill's fallback (and vice versa).
    /// </summary>
    private static string ChamberOf(string slug) => slug switch
    {
        "nomination" => "nomination",
        _ when slug.StartsWith("senate", StringComparison.Ordinal) => "senate",
        _ => "house"
    };

    /// <summary>Yields every normalized bill id ("slug:number") found in a block of text.</summary>
    private static IEnumerable<string> EnumerateIds(string text)
    {
        foreach (Match m in BillRegex().Matches(text))
        {
            var slug = SlugForType(m.Groups["type"].Value);
            if (slug is null) continue;
            foreach (Match num in NumberRegex().Matches(m.Groups["nums"].Value))
                yield return $"{slug}:{num.Value}";
        }
    }

    /// <summary>Maps a matched bill-type token (abbreviated or spelled-out) to its congress.gov URL segment.</summary>
    private static string? SlugForType(string typeToken)
    {
        var lower = typeToken.ToLowerInvariant();
        if (lower.Contains("senate") || lower.Contains("house"))
        {
            var chamber = lower.Contains("senate") ? "senate" : "house";
            if (lower.Contains("resolution"))
            {
                if (lower.Contains("joint")) return $"{chamber}-joint-resolution";
                if (lower.Contains("concurrent")) return $"{chamber}-concurrent-resolution";
                return $"{chamber}-resolution";
            }
            if (lower.Contains("bill")) return $"{chamber}-bill";
            return null;
        }

        // Abbreviated forms: normalize to letters only (dots/spaces dropped) and map.
        var key = new string(typeToken.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        return key switch
        {
            "PN" => "nomination",
            "S" => "senate-bill",
            "HR" => "house-bill",
            "SRES" => "senate-resolution",
            "HRES" => "house-resolution",
            "SJRES" => "senate-joint-resolution",
            "HJRES" => "house-joint-resolution",
            "SCONRES" => "senate-concurrent-resolution",
            "HCONRES" => "house-concurrent-resolution",
            _ => null
        };
    }

    private static string Url(int congress, string slug, string number) =>
        slug == "nomination"
            ? $"https://www.congress.gov/nomination/{Ordinal(congress)}-congress/{number}"
            : $"https://www.congress.gov/bill/{Ordinal(congress)}-congress/{slug}/{number}";

    private static string Anchor(string url, string text) =>
        $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer\">{text}</a>";

    /// <summary>Congress derived from the most recent parseable source date (else today's date).</summary>
    private static int FallbackCongress(IReadOnlyList<RetrievedChunk> sources)
    {
        DateOnly? latest = null;
        foreach (var s in sources)
            if (DateOnly.TryParse(s.MeetingDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && (latest is null || d > latest))
                latest = d;
        return CongressFromDate(latest ?? DateOnly.FromDateTime(DateTime.UtcNow));
    }

    private static int? CongressFor(string? isoDate) =>
        DateOnly.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? CongressFromDate(d)
            : null;

    /// <summary>
    /// Each congress spans two years starting Jan 3 of the odd year (1st = 1789). e.g. 2025/2026 -> 119th.
    /// </summary>
    private static int CongressFromDate(DateOnly d)
    {
        var year = d.Year;
        if (year % 2 == 0) year--;                          // even year belongs to the odd year's congress
        else if (d < new DateOnly(year, 1, 3)) year -= 2;   // before Jan 3 -> previous congress
        return (year - 1789) / 2 + 1;
    }

    private static string Ordinal(int n)
    {
        var suffix = (n % 100) is >= 11 and <= 13
            ? "th"
            : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{n}{suffix}";
    }

    // A bill-type token (spelled-out forms and abbreviations, longest/most-specific first), optionally
    // "No(s).", then one or more numbers separated by commas / semicolons / "and" / "&".
    [GeneratedRegex(
        @"\b(?<type>Senate\s+Joint\s+Resolutions?|House\s+Joint\s+Resolutions?|Senate\s+Concurrent\s+Resolutions?|House\s+Concurrent\s+Resolutions?|Senate\s+Resolutions?|House\s+Resolutions?|Senate\s+Bills?|House\s+Bills?|S\.?\s?J\.?\s?Res\.?|H\.?\s?J\.?\s?Res\.?|S\.?\s?Con\.?\s?Res\.?|H\.?\s?Con\.?\s?Res\.?|S\.?\s?Res\.?|H\.?\s?Res\.?|H\.?\s?R\.?|PN\.?|S\.)\s*(?:Nos?\.?\s*)?(?<nums>\d{1,5}(?:(?:\s*(?:,|;|&|and)\s*)+\d{1,5})*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BillRegex();

    [GeneratedRegex(@"\d{1,5}")]
    private static partial Regex NumberRegex();
}
