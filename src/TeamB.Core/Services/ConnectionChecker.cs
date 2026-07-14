using System.Diagnostics;
using Microsoft.Extensions.Options;
using TeamB.Core.Configuration;

namespace TeamB.Core.Services;

/// <summary>Health of a single external dependency.</summary>
public sealed record DependencyStatus(string Name, bool Ok, string Detail, long ElapsedMs);

/// <summary>Result of a full connectivity preflight.</summary>
public sealed record ConnectionReport(IReadOnlyList<DependencyStatus> Dependencies)
{
    public bool AllOk => Dependencies.All(d => d.Ok);
}

public interface IConnectionChecker
{
    /// <summary>Fires a tiny request at each dependency and reports OK/failed with an actionable reason.</summary>
    Task<ConnectionReport> CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// Preflight check for the app's external dependencies (embeddings, chat, search). Lets the user verify
/// everything is reachable before ingesting, and surfaces the specific failure instead of a raw stack.
/// </summary>
public sealed class ConnectionChecker : IConnectionChecker
{
    private readonly IEmbeddingService _embeddings;
    private readonly IGenerationService _generation;
    private readonly ISearchService _search;
    private readonly AzureOpenAIOptions _openAi;

    public ConnectionChecker(
        IEmbeddingService embeddings,
        IGenerationService generation,
        ISearchService search,
        IOptions<AzureOpenAIOptions> openAi)
    {
        _embeddings = embeddings;
        _generation = generation;
        _search = search;
        _openAi = openAi.Value;
    }

    public async Task<ConnectionReport> CheckAsync(CancellationToken ct = default)
    {
        var results = new List<DependencyStatus>
        {
            await ProbeAsync($"Embeddings ({_openAi.EmbeddingDeployment})", async () =>
            {
                var v = await _embeddings.EmbedAsync("ping", ct);
                return $"OK — {v.Length}-dim vector";
            }),
            await ProbeAsync($"Chat ({_openAi.ChatDeployment})", async () =>
            {
                await _generation.CompleteAsync("You are a health probe. Reply with 'ok'.", "ping", ct);
                return "OK";
            }),
            await ProbeAsync("Search", async () =>
            {
                var index = await _search.GetActiveIndexNameAsync(ct);
                var count = await _search.GetDocumentCountAsync(ct);
                return index is null
                    ? "OK — no index yet (ingest to create one)"
                    : $"OK — {count} passage(s) in '{index}'";
            })
        };

        return new ConnectionReport(results);
    }

    private static async Task<DependencyStatus> ProbeAsync(string name, Func<Task<string>> probe)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var detail = await probe();
            sw.Stop();
            return new DependencyStatus(name, true, detail, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DependencyStatus(name, false, AzureErrorHelper.Describe(ex), sw.ElapsedMilliseconds);
        }
    }
}
