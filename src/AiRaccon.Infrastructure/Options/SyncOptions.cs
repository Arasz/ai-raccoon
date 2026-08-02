namespace AiRaccon.Infrastructure.Options;

/// <summary>Sync credentials come from config or environment only; never hardcoded or defaulted.</summary>
public sealed record SyncOptions
{
    public string? ManagedDatabaseId { get; init; }

    public string? ApiKey { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ManagedDatabaseId) && !string.IsNullOrWhiteSpace(ApiKey);
}
