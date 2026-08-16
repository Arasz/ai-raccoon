namespace AiRaccoon.Core.Sync;

/// <summary>Thrown when memory sync is not configured or its connection string is invalid.</summary>
public sealed class SyncNotConfiguredException : InvalidOperationException
{
    private const string DefaultMessage =
        "Memory sync is not configured or its connection string is invalid. Run 'ai-raccoon settings sync add s3 <url> --bucket <name>' or 'ai-raccoon settings sync add azure <container>' and enter the credentials when prompted.";

    public SyncNotConfiguredException()
        : base(DefaultMessage)
    {
    }

    public SyncNotConfiguredException(Exception inner)
        : base(DefaultMessage, inner)
    {
    }
}
