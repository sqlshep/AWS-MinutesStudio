using System.Net;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace TeamB.Web;

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
    /// Plain-text form for copying: drops injected HTML tags (bill links, citation preview spans)
    /// while keeping their visible text, so the clipboard has no URLs or hidden preview snippets.
    /// </summary>
    public static string ToCopyText(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : HtmlTag().Replace(text, string.Empty);

    [GeneratedRegex("```mermaid\\s*\\n([\\s\\S]*?)```")]
    private static partial Regex MermaidBlock();

    [GeneratedRegex("</?(?:a|span)\\b[^>]*>")]
    private static partial Regex HtmlTag();
}
