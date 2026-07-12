namespace TeamB.Core.Models;

/// <summary>A chunk returned from the vector store together with its relevance score.</summary>
public sealed class RetrievedChunk
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public required string SourceFile { get; init; }
    public required string Title { get; init; }
    public string? MeetingDate { get; init; }
    public int ChunkIndex { get; init; }
    public double Score { get; init; }
}
