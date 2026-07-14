namespace TeamB.Core.Models;

/// <summary>How long the generated work product should be, relative to the template default.</summary>
public enum WorkProductLength
{
    Brief,
    Standard,
    Detailed
}

/// <summary>Register/voice adjustment layered on top of the template's built-in tone.</summary>
public enum WorkProductTone
{
    Default,
    Formal,
    Conversational
}

/// <summary>
/// Caller-selectable style knobs for a work product. These adjust the prompt's tone and length
/// only — they never change the grounding rules, required sections, or any facts/figures/citations.
/// </summary>
public sealed record GenerationOptions(
    WorkProductLength Length = WorkProductLength.Standard,
    WorkProductTone Tone = WorkProductTone.Default)
{
    public static GenerationOptions Default { get; } = new();
}
