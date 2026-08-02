namespace MinutesStudio.Core.Models;

/// <summary>
/// The four Minutes Studio work products the prompt library targets. Each maps to a
/// distinct annotated prompt template in <see cref="Prompts.PromptLibrary"/>.
/// </summary>
public enum WorkProductType
{
    StakeholderBrief,
    ExecutiveTalkingPoints,
    MeetingSummary,
    PlainLanguageSummary
}
