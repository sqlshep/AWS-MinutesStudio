using System.ClientModel;
using Azure;

namespace TeamB.Core.Services;

/// <summary>
/// Small retry helper with exponential backoff. Covers the usual transient statuses (408/429/5xx) plus
/// 404 — this Foundry resource intermittently returns 404 (DeploymentNotFound) for a deployment that
/// actually exists, so we treat it as transient rather than failing the whole operation.
/// </summary>
public static class Retry
{
    public static async Task<T> OnTransientAsync<T>(
        Func<Task<T>> action,
        int maxAttempts = 5,
        int baseDelayMs = 250,
        CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                var delayMs = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                await Task.Delay(delayMs, ct);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        ClientResultException cre => IsTransientStatus(cre.Status),
        RequestFailedException rfe => IsTransientStatus(rfe.Status),
        HttpRequestException => true,
        TaskCanceledException => true,
        TimeoutException => true,
        _ => false
    };

    private static bool IsTransientStatus(int status) =>
        status is 404 or 408 or 429 || status >= 500;
}
