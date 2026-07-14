using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using TeamB.Core.Models;

namespace TeamB.Core.Services;

/// <summary>
/// Post-processes a generated work product and attaches a short source excerpt to each
/// "[Meeting Title]" citation. The excerpt is chosen from that meeting's source text by keyword
/// overlap with the sentence the citation is attached to, and is embedded in a data-preview
/// attribute so the UI can show it as a hover/focus tooltip (no markdown re-parsing, no JS).
/// </summary>
public interface ICitationPreviewer
{
    string AddPreviews(string content, IReadOnlyList<RetrievedChunk> chunks);
}

public sealed partial class CitationPreviewer : ICitationPreviewer
{
    private const int MaxSnippet = 1200;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","that","this","with","from","were","was","have","has","had","for","are","but",
        "not","which","their","them","they","also","into","other","about","would","could","been",
        "then","than","when","what","who","whom","its","it's","said","meeting","business","committee"
    };

    public string AddPreviews(string content, IReadOnlyList<RetrievedChunk> chunks)
    {
        if (string.IsNullOrWhiteSpace(content) || chunks.Count == 0) return content;

        // Full text per meeting title (chunks reassembled in order).
        var byTitle = chunks
            .Where(c => !string.IsNullOrEmpty(c.Content))
            .GroupBy(c => Normalize(c.Title), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => string.Join(" ", g.OrderBy(c => c.ChunkIndex).Select(c => c.Content)),
                StringComparer.OrdinalIgnoreCase);

        if (byTitle.Count == 0) return content;

        return CitationRegex().Replace(content, match =>
        {
            var title = ResolveTitle(match.Groups[1].Value, byTitle);
            if (title is null || !byTitle.TryGetValue(title, out var sourceText)) return match.Value;

            var claim = PrecedingClaim(content, match.Index);
            var snippet = BestSnippet(sourceText, claim);
            if (string.IsNullOrEmpty(snippet)) return match.Value;

            var encoded = WebUtility.HtmlEncode(snippet);
            var label = WebUtility.HtmlEncode(match.Value);
            return $"<span class=\"cite\" role=\"button\" tabindex=\"0\" data-cite=\"{label}\" data-preview=\"{encoded}\">{match.Value}</span>";
        });
    }

    private static readonly HashSet<string> CitationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "sourcing", "source", "sources", "see", "ref", "refs", "reference", "references", "cite", "citation"
    };

    /// <summary>
    /// Maps the text inside a "[...]" citation to a known meeting title. Handles the standard
    /// "[Meeting — Date]" form as well as labeled variants like "[Sourcing: Meeting — Date]"
    /// (used by Executive Talking Points), and falls back to a contained-title match.
    /// </summary>
    private static string? ResolveTitle(string inner, IReadOnlyDictionary<string, string> byTitle)
    {
        var normalized = Normalize(inner);
        if (byTitle.ContainsKey(normalized)) return normalized;

        // Strip a leading label like "Sourcing:" / "Source:" / "See:" and retry.
        var colon = normalized.IndexOf(':');
        if (colon > 0 && CitationLabels.Contains(normalized[..colon].Trim()))
        {
            var candidate = normalized[(colon + 1)..].Trim();
            if (byTitle.ContainsKey(candidate)) return candidate;
            normalized = candidate;
        }

        // Fallback: a known meeting title contained within the citation text.
        return byTitle.Keys.FirstOrDefault(
            key => normalized.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The ~220 chars of answer text before the citation, tags stripped — used as the query.</summary>
    private static string PrecedingClaim(string content, int citationIndex)
    {
        var before = content[..citationIndex];
        before = TagRegex().Replace(before, " ");
        if (before.Length > 220) before = before[^220..];
        // Keep only the trailing sentence/bullet so the match focuses on the current claim.
        var start = before.LastIndexOfAny(new[] { '.', '!', '?', '\n', '•', '-' });
        return start >= 0 ? before[(start + 1)..] : before;
    }

    /// <summary>Picks the source segment that best overlaps the claim's significant words.</summary>
    private static string BestSnippet(string sourceText, string claim)
    {
        var keywords = Tokenize(claim);
        var segments = SegmentRegex().Split(sourceText)
            .Select(s => s.Trim())
            .Where(s => s.Length >= 20)
            .ToList();
        if (segments.Count == 0) return string.Empty;

        var bestScore = 0;
        var bestIndex = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var words = Tokenize(segments[i]);
            var score = keywords.Count == 0 ? 0 : words.Count(keywords.Contains);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        // Build a roomy excerpt for the side panel: start one segment early for lead-in context,
        // then extend forward until we approach the length budget.
        var startIndex = bestIndex > 0 ? bestIndex - 1 : 0;
        var sb = new StringBuilder();
        var lastIndex = startIndex;
        for (var i = startIndex; i < segments.Count; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(segments[i]);
            lastIndex = i;
            if (sb.Length >= MaxSnippet) break;
        }

        var snippet = WhitespaceRegex().Replace(sb.ToString(), " ").Trim();
        if (snippet.Length > MaxSnippet) snippet = snippet[..MaxSnippet].TrimEnd() + "\u2026";
        else if (lastIndex < segments.Count - 1) snippet += "\u2026";
        if (startIndex > 0) snippet = "\u2026" + snippet;
        return snippet;
    }

    private static HashSet<string> Tokenize(string text) =>
        WordRegex().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string s) => WhitespaceRegex().Replace(s, " ").Trim();

    // Citations look like "[Business Meeting — January 29, 2026]".
    [GeneratedRegex(@"\[([^\[\]\n]{4,120})\]")]
    private static partial Regex CitationRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z']+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
