namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Thrown when memory sync is requested without managed database credentials (spec §7).</summary>
public sealed class SyncNotConfiguredException : InvalidOperationException
{
    public SyncNotConfiguredException()
        : base("Memory sync is not configured. Set AIRACCOON_SQLITECLOUD_DB_ID and AIRACCOON_SQLITECLOUD_API_KEY.")
    {
    }
}
