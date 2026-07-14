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
    private const int MaxSnippet = 300;

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
            var title = Normalize(match.Groups[1].Value);
            if (!byTitle.TryGetValue(title, out var sourceText)) return match.Value;

            var claim = PrecedingClaim(content, match.Index);
            var snippet = BestSnippet(sourceText, claim);
            if (string.IsNullOrEmpty(snippet)) return match.Value;

            var encoded = WebUtility.HtmlEncode(snippet);
            return $"<span class=\"cite\" tabindex=\"0\" data-preview=\"{encoded}\">{match.Value}</span>";
        });
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

        // No keyword overlap (e.g. a generic claim): fall back to the meeting's opening line.
        var snippet = segments[bestIndex];
        if (snippet.Length < 120 && bestIndex + 1 < segments.Count)
            snippet = snippet + " " + segments[bestIndex + 1];

        snippet = WhitespaceRegex().Replace(snippet, " ").Trim();
        if (snippet.Length > MaxSnippet) snippet = snippet[..MaxSnippet].TrimEnd() + "\u2026";
        if (bestIndex > 0) snippet = "\u2026" + snippet;
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
