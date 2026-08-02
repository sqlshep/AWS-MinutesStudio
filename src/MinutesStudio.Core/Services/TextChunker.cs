using System.Text;
using System.Text.RegularExpressions;

namespace MinutesStudio.Core.Services;

/// <summary>A chunk of text with the page range it was drawn from.</summary>
public sealed record TextChunk(string Content, int PageStart, int PageEnd, int Index);

public interface ITextChunker
{
    IReadOnlyList<TextChunk> Chunk(IReadOnlyList<PageText> pages, int chunkSizeChars, int overlapChars);
}

/// <summary>
/// Character-based sliding-window chunker with overlap. Prefers to break on paragraph or
/// sentence boundaries near the target size so chunks stay semantically coherent, and it
/// tracks page ranges so each chunk can be cited back to its source pages.
/// </summary>
public sealed partial class TextChunker : ITextChunker
{
    public IReadOnlyList<TextChunk> Chunk(IReadOnlyList<PageText> pages, int chunkSizeChars, int overlapChars)
    {
        if (chunkSizeChars <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSizeChars));
        if (overlapChars < 0 || overlapChars >= chunkSizeChars)
            throw new ArgumentOutOfRangeException(nameof(overlapChars), "Overlap must be >= 0 and < chunk size.");

        // Build a single normalized string and remember where each page starts.
        var sb = new StringBuilder();
        var pageStartOffsets = new List<(int Offset, int PageNumber)>();
        foreach (var page in pages)
        {
            var cleaned = Normalize(page.Text);
            if (cleaned.Length == 0) continue;
            pageStartOffsets.Add((sb.Length, page.PageNumber));
            sb.Append(cleaned).Append("\n\n");
        }

        var full = sb.ToString();
        var chunks = new List<TextChunk>();
        if (full.Length == 0) return chunks;

        var index = 0;
        var pos = 0;
        while (pos < full.Length)
        {
            var end = Math.Min(pos + chunkSizeChars, full.Length);
            if (end < full.Length)
                end = FindBreakPoint(full, pos, end);

            var content = full[pos..end].Trim();
            if (content.Length > 0)
            {
                var pageStart = PageAt(pageStartOffsets, pos);
                var pageEnd = PageAt(pageStartOffsets, end - 1);
                chunks.Add(new TextChunk(content, pageStart, pageEnd, index++));
            }

            if (end >= full.Length) break;
            pos = Math.Max(end - overlapChars, pos + 1);
        }

        return chunks;
    }

    /// <summary>Look for a paragraph/sentence boundary in the back portion of the window for a clean cut.</summary>
    private static int FindBreakPoint(string text, int start, int hardEnd)
    {
        var minBreak = start + (hardEnd - start) / 2; // don't break earlier than halfway
        var paragraph = text.LastIndexOf("\n\n", hardEnd - 1, hardEnd - start, StringComparison.Ordinal);
        if (paragraph >= minBreak) return paragraph + 2;

        for (var i = hardEnd - 1; i >= minBreak; i--)
        {
            var c = text[i];
            if (c is '.' or '!' or '?' or '\n')
                return i + 1;
        }

        return hardEnd;
    }

    private static int PageAt(List<(int Offset, int PageNumber)> offsets, int position)
    {
        var page = offsets.Count > 0 ? offsets[0].PageNumber : 1;
        foreach (var (offset, pageNumber) in offsets)
        {
            if (offset > position) break;
            page = pageNumber;
        }

        return page;
    }

    /// <summary>Collapse excess whitespace so token budgets aren't wasted on layout artifacts.</summary>
    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var collapsed = WhitespaceRegex().Replace(text, " ");
        return collapsed.Trim();
    }

    [GeneratedRegex(@"[ \t\f\r]+")]
    private static partial Regex WhitespaceRegex();
}
