namespace MinutesStudio.Core.Models;

/// <summary>
/// A chunk of a source document, ready to embed and index. This is the unit
/// that gets stored in the vector store and later retrieved for grounding.
/// </summary>
public sealed class DocumentChunk
{
    public required string Id { get; init; }
    public required string Content { get; init; }

    /// <summary>Original file name, e.g. "04 15 26 -- Business Meeting.pdf".</summary>
    public required string SourceFile { get; init; }

    /// <summary>Human-friendly title used in citations, e.g. "Business Meeting — April 15, 2026".</summary>
    public required string Title { get; init; }

    /// <summary>Best-effort meeting date parsed from the file name (ISO yyyy-MM-dd), if available.</summary>
    public string? MeetingDate { get; init; }

    public int ChunkIndex { get; init; }
    public int PageStart { get; init; }
    public int PageEnd { get; init; }

    /// <summary>Embedding vector for the content. Populated by the embedding service.</summary>
    public ReadOnlyMemory<float> ContentVector { get; set; }
}
