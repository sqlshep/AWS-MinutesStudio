namespace TeamB.Core.Configuration;

/// <summary>
/// Connection settings for Azure Blob Storage, the source of the meeting-minute PDFs (Phase 4).
/// Bind from configuration section "AzureBlob". The connection string is a secret and should come
/// from user-secrets locally (or Key Vault / managed identity in Azure).
/// </summary>
public sealed class AzureBlobOptions
{
    public const string SectionName = "AzureBlob";

    /// <summary>Storage account connection string. Required for the prototype's connection-string auth.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Container that holds the source PDFs.</summary>
    public string ContainerName { get; set; } = "meeting-minutes";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}
