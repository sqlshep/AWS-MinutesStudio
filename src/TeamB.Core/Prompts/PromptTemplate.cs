using TeamB.Core.Models;

namespace TeamB.Core.Prompts;

/// <summary>
/// A single work-product prompt. Keeps the system prompt, the user-prompt
/// composition, and the human-readable design annotations together so the
/// prompt library doubles as the "annotated prompts" interview deliverable.
/// </summary>
public sealed class PromptTemplate
{
    public required WorkProductType WorkProduct { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>One-line description shown in the UI picker.</summary>
    public required string Description { get; init; }

    /// <summary>The system prompt: role, guardrails, and output contract.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// Short rationale for the key design choices in this prompt. Surfaced in the UI
    /// and exported to the one-page write-up. This is the "annotate each prompt" ask.
    /// </summary>
    public required string DesignNotes { get; init; }

    /// <summary>
    /// Composes the user turn from the caller's request and the retrieved, pre-formatted context.
    /// Keeping context in the user turn (not the system prompt) makes grounding auditable per request.
    /// </summary>
    public string BuildUserPrompt(string request, string context) =>
        $"""
        REQUEST FROM TEAM MEMBER:
        {request}

        ---
        SOURCE MATERIAL (retrieved committee meeting minutes; cite these and only these):
        {context}
        ---

        Produce the {DisplayName} now, following all formatting and grounding rules.
        """;
}
