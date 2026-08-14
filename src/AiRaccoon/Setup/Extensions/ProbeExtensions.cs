using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;

namespace AiRaccoon.Setup.Extensions;

public static class ProbeExtensions
{
    extension(ISqliteConnectionFactory factory)
    {
        public async Task<EncryptionProbeResult> ProbeUsingEncryptionKey(string? key, CancellationToken ctx)
        {
            try
            {
                await using var probe = await factory.OpenBankWithKeyAsync(key, ctx);
                return EncryptionProbeResult.Success;
            }
            catch (Exception e)
            {
                return EncryptionProbeResult.Failure(e);
            }
        }
    }

    extension(IEncryptionKeyResolver encryptionKeyResolver)
    {
        public async Task<EncryptionKeyProbeResult> ProbeResolvingEncryptionKeyAsync(CancellationToken cancellationToken)
        {
            try
            {
                return EncryptionKeyProbeResult.Success(
                    await encryptionKeyResolver.ResolveAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                return EncryptionKeyProbeResult.Failure(ex);
            }
        }
    }

    public sealed record EncryptionProbeResult(bool IsCorrectKey)
    {
        public static readonly EncryptionProbeResult Success = new(true);

        public Exception? Exception { get; private init; }

        public static EncryptionProbeResult Failure(Exception exception) =>
            new(false)
            {
                Exception = exception
            };
    }

    public sealed record EncryptionKeyProbeResult(ResolvedKey Key)
    {
        public bool IsSuccess => Key != ResolvedKey.None;

        public Exception? Exception { get; init; }
        public static EncryptionKeyProbeResult Success(ResolvedKey key) => new(key);

        public static EncryptionKeyProbeResult Failure(Exception exception) =>
            new(ResolvedKey.None)
            {
                Exception = exception
            };
    }
}
