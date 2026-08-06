namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

public interface IEncryptionKeyResolver
{
    ResolvedKey Resolve();
}
