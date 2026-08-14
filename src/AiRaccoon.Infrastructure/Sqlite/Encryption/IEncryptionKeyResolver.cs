namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

public interface IEncryptionKeyResolver
{
    Task<ResolvedKey> ResolveAsync(CancellationToken cancellationToken = default);
}
