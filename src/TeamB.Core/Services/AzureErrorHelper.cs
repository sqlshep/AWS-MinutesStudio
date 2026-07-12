using System.ClientModel;
using Azure;

namespace TeamB.Core.Services;

/// <summary>
/// Turns raw Azure/OpenAI SDK exceptions into short, actionable messages for the UI and logs.
/// Distinguishes auth problems, missing/provisioning deployments, rate limits, and connectivity.
/// </summary>
public static class AzureErrorHelper
{
    public static string Describe(Exception ex)
    {
        var status = ex switch
        {
            ClientResultException cre => cre.Status,       // Azure OpenAI (System.ClientModel)
            RequestFailedException rfe => rfe.Status,       // Azure AI Search
            _ => 0
        };

        return status switch
        {
            401 or 403 =>
                "Authentication failed (401/403). The API key may be wrong or expired, or the identity lacks access. "
                + "Re-check the configured key / role assignment.",
            404 =>
                "Resource not found (404). The deployment or index name may be wrong, or a just-created "
                + "deployment is still provisioning — wait a moment and retry.",
            429 =>
                "Rate limited (429). The service is throttling requests — retry shortly or raise the quota.",
            >= 500 =>
                $"The Azure service returned a server error ({status}). This is usually transient — retry shortly.",
            _ when IsConnectivity(ex) =>
                "Could not reach the service. Check the endpoint URL and network connectivity.",
            _ => ex.Message
        };
    }

    private static bool IsConnectivity(Exception ex) =>
        ex is HttpRequestException
        || ex.InnerException is HttpRequestException
        || ex is TaskCanceledException or TimeoutException;
}
