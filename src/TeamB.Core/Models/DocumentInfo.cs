namespace TeamB.Core.Models;

/// <summary>Summary of a single ingested source document (one meeting), for pickers and listings.</summary>
public sealed class DocumentInfo
{
    public required string SourceFile { get; init; }
    public required string Title { get; init; }
    public string? MeetingDate { get; init; }
    public int ChunkCount { get; init; }
}
