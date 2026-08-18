using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Options;
using MinutesStudio.Core.Configuration;
using MinutesStudio.Core.Models;
using MinutesStudio.Core.Prompts;
using TokenUsage = MinutesStudio.Core.Models.TokenUsage;

namespace MinutesStudio.Core.Services;

/// <summary>A completed generation: the text plus the tokens it consumed.</summary>
public sealed record CompletionResult(string Text, TokenUsage Usage);

public interface IGenerationService
{
    Task<CompletionResult> CompleteAsync(PromptTemplate template, string request, string context, CancellationToken ct = default);

    /// <summary>Runs a completion from an explicit system + user prompt (used by document chat).</summary>
    Task<CompletionResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}

/// <summary>Runs a grounded chat completion against an Amazon Bedrock model via the Converse API.</summary>
public sealed class BedrockGenerationService : IGenerationService
{
    private readonly IAmazonBedrockRuntime _client;
    private readonly string _modelId;
    private readonly int _maxTokens;

    public BedrockGenerationService(IAmazonBedrockRuntime client, IOptions<BedrockOptions> options)
    {
        _client = client;
        _modelId = options.Value.ChatModelId;
        _maxTokens = options.Value.MaxOutputTokens;
    }

    public Task<CompletionResult> CompleteAsync(
        PromptTemplate template, string request, string context, CancellationToken ct = default) =>
        CompleteAsync(template.SystemPrompt, template.BuildUserPrompt(request, context), ct);

    public async Task<CompletionResult> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var request = new ConverseRequest
        {
            ModelId = _modelId,
            System = new List<SystemContentBlock> { new() { Text = systemPrompt } },
            Messages = new List<Message>
            {
                new()
                {
                    Role = ConversationRole.User,
                    Content = new List<ContentBlock> { new() { Text = userPrompt } }
                }
            },
            // Converse exposes only the common inference params; Nova requires MaxTokens.
            InferenceConfig = new InferenceConfiguration { MaxTokens = _maxTokens }
        };

        var response = await Retry.OnTransientAsync(() => _client.ConverseAsync(request, ct), ct: ct);

        var text = string.Concat(
            (response.Output?.Message?.Content ?? new List<ContentBlock>())
                .Select(part => part.Text)
                .Where(t => !string.IsNullOrEmpty(t)));

        var u = response.Usage;
        var usage = u is null
            ? TokenUsage.Zero
            : new TokenUsage(u.InputTokens ?? 0, u.OutputTokens ?? 0, u.TotalTokens ?? 0);

        return new CompletionResult(text, usage);
    }
}
