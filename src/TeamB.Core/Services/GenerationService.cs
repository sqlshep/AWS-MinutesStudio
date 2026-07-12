using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using TeamB.Core.Configuration;
using TeamB.Core.Prompts;

namespace TeamB.Core.Services;

public interface IGenerationService
{
    Task<string> CompleteAsync(PromptTemplate template, string request, string context, CancellationToken ct = default);

    /// <summary>Runs a completion from an explicit system + user prompt (used by document chat).</summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}

/// <summary>Runs a grounded chat completion against the Azure OpenAI (Foundry) chat deployment.</summary>
public sealed class AzureOpenAIGenerationService : IGenerationService
{
    private readonly ChatClient _client;

    public AzureOpenAIGenerationService(AzureOpenAIClient client, IOptions<AzureOpenAIOptions> options)
    {
        _client = client.GetChatClient(options.Value.ChatDeployment);
    }

    public Task<string> CompleteAsync(
        PromptTemplate template, string request, string context, CancellationToken ct = default) =>
        CompleteAsync(template.SystemPrompt, template.BuildUserPrompt(request, context), ct);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        // GPT-5 family notes: (1) only the default temperature is accepted, and (2) this SDK
        // version serializes a token cap as the legacy "max_tokens", which these models reject
        // in favor of "max_completion_tokens" — so we omit the cap and let the prompt bound length.
        var completion = await Retry.OnTransientAsync(
            () => _client.CompleteChatAsync(messages, cancellationToken: ct), ct: ct);
        return string.Concat(completion.Value.Content.Select(part => part.Text));
    }
}
