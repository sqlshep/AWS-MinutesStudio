namespace TeamB.Core.Models;

/// <summary>Answer to a free-form document chat question, with the passages it was grounded on.</summary>
public sealed class ChatAnswer
{
    public required string Content { get; init; }
    public required IReadOnlyList<RetrievedChunk> Sources { get; init; }
    public string? Warning { get; init; }
}
