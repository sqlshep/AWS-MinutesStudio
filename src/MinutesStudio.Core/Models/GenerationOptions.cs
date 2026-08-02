namespace MinutesStudio.Core.Models;

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

/// <summary>How source references appear in a generated work product.</summary>
public enum ReferenceMode
{
    /// <summary>Inline clickable citations plus the Sources section (default).</summary>
    Included,

    /// <summary>The prompt asks the model to omit citations and the Sources section.</summary>
    Hidden,

    /// <summary>
    /// Like <see cref="Hidden"/>, but additionally strips any residual citation markers from the
    /// generated text after the fact — guaranteeing a reference-free draft safe to copy or email.
    /// </summary>
    Clean
}

/// <summary>
/// Caller-selectable style knobs for a work product. Tone and length adjust the prompt's register
/// only — they never change the grounding rules or any facts/figures. <see cref="References"/>
/// controls whether inline citations and the Sources section appear in the output.
/// </summary>
public sealed record GenerationOptions(
    WorkProductLength Length = WorkProductLength.Standard,
    WorkProductTone Tone = WorkProductTone.Default,
    ReferenceMode References = ReferenceMode.Included)
{
    public static GenerationOptions Default { get; } = new();
}
