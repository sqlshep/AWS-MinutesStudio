namespace TeamB.Core.Models;

/// <summary>Output of a RAG generation: the drafted work product plus the sources it was grounded on.</summary>
public sealed class GenerationResult
{
    public required string Content { get; init; }
    public required WorkProductType WorkProduct { get; init; }
    public required IReadOnlyList<RetrievedChunk> Sources { get; init; }

    /// <summary>Total tokens consumed generating this work product (summed across map-reduce calls).</summary>
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;

    /// <summary>Set when generation could not be completed (e.g. no relevant sources found).</summary>
    public string? Warning { get; init; }
}
