namespace MinutesStudio.Core.Models;

/// <summary>Token accounting for a generation. Sums cleanly across multiple calls (e.g. map-reduce).</summary>
public readonly record struct TokenUsage(int InputTokens, int OutputTokens, int TotalTokens)
{
    public static TokenUsage Zero => new(0, 0, 0);

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, a.TotalTokens + b.TotalTokens);
}
