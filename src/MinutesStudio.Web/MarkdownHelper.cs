using System.Net;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace MinutesStudio.Web;

/// <summary>Renders model-produced markdown to sanitized HTML for display.</summary>
public static partial class MarkdownHelper
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static MarkupString ToHtml(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown)
            ? new MarkupString(string.Empty)
            : new MarkupString(Markdown.ToHtml(markdown, Pipeline));

    /// <summary>
    /// Renders markdown to HTML, preserving ```mermaid fenced blocks as &lt;pre class="mermaid"&gt;
    /// elements so the Mermaid client library can draw them as diagrams.
    /// </summary>
    public static MarkupString ToHtmlWithDiagrams(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return new MarkupString(string.Empty);

        // Pull the raw mermaid blocks out first (Markdig would HTML-escape and wrap them as code).
        var diagrams = new List<string>();
        var stripped = MermaidBlock().Replace(markdown, match =>
        {
            diagrams.Add(match.Groups[1].Value.Trim());
            return $"\n\nMERMAIDPLACEHOLDER{diagrams.Count - 1}\n\n";
        });

        var html = Markdown.ToHtml(stripped, Pipeline);

        for (var i = 0; i < diagrams.Count; i++)
        {
            // HTML-encode the graph text; the browser decodes entities in textContent, which Mermaid reads.
            var block = $"<pre class=\"mermaid\">{WebUtility.HtmlEncode(diagrams[i])}</pre>";
            html = html
                .Replace($"<p>MERMAIDPLACEHOLDER{i}</p>", block)
                .Replace($"MERMAIDPLACEHOLDER{i}", block);
        }

        return new MarkupString(html);
    }

    /// <summary>
    /// Rich HTML form for copying: renders the markdown to HTML and drops the injected anchor/span
    /// tags (bill links, citation preview spans) while keeping their visible text. Pasting this into
    /// an email client or Word preserves headings, bold, and bullet formatting — no raw markdown.
    /// </summary>
    public static string ToCopyHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var html = Markdown.ToHtml(text, Pipeline);
        return HtmlTag().Replace(html, string.Empty);
    }

    /// <summary>
    /// Plain-text fallback for copying: drops injected HTML tags and strips markdown syntax
    /// (headers, bold/italic, bullets, inline code, links) so plain-text editors get clean prose.
    /// </summary>
    public static string ToCopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var s = HtmlTag().Replace(text, string.Empty);          // drop injected <a>/<span>
        s = MdHeader().Replace(s, "$1");                          // "## Title" -> "Title"
        s = MdLink().Replace(s, "$1");                            // "[text](url)" -> "text"
        s = MdBold().Replace(s, "$1$2");                          // **x** / __x__ -> x
        s = MdItalic().Replace(s, "$1$2");                        // *x* / _x_ -> x
        s = MdInlineCode().Replace(s, "$1");                     // `code` -> code
        s = MdBullet().Replace(s, "$1\u2022 ");                  // "- item" -> "• item"
        s = ExtraBlankLines().Replace(s, "\n\n");                // collapse 3+ newlines
        return s.Trim();
    }

    [GeneratedRegex("```mermaid\\s*\\n([\\s\\S]*?)```")]
    private static partial Regex MermaidBlock();

    [GeneratedRegex("</?(?:a|span)\\b[^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s+(.*)$")]
    private static partial Regex MdHeader();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MdLink();

    [GeneratedRegex(@"\*\*([^*]+)\*\*|__([^_]+)__")]
    private static partial Regex MdBold();

    [GeneratedRegex(@"\*([^*\n]+)\*|(?<![A-Za-z0-9])_([^_\n]+)_(?![A-Za-z0-9])")]
    private static partial Regex MdItalic();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex MdInlineCode();

    [GeneratedRegex(@"(?m)^(\s*)[-*+]\s+")]
    private static partial Regex MdBullet();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();
}
