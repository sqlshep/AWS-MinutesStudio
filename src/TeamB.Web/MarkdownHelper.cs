using Markdig;
using Microsoft.AspNetCore.Components;

namespace TeamB.Web;

/// <summary>Renders model-produced markdown to sanitized HTML for display.</summary>
public static class MarkdownHelper
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static MarkupString ToHtml(string? markdown) =>
        string.IsNullOrWhiteSpace(markdown)
            ? new MarkupString(string.Empty)
            : new MarkupString(Markdown.ToHtml(markdown, Pipeline));
}
