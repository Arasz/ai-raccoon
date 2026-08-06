using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Tests;

/// <summary>
///     Trait values for filtering: `dotnet test --filter "Category=Unit&Speed=Fast"`.
///     Category: Unit (pure logic / fakes), Integration (real SQLite or native extensions),
///     E2E (full server over HTTP via WebApplicationFactory). Speed: Fast vs Slow.
/// </summary>
public static class TestCategories
{
    public const string Category = "Category";
    public const string Unit = "Unit";
    public const string Integration = "Integration";
    public const string E2E = "E2E";
    public const string Retrieval = "Retrieval";

    public const string Speed = "Speed";
    public const string Fast = "Fast";
    public const string Slow = "Slow";
}

/// <summary>Returns null — no encryption. Use for existing unencrypted-DB tests.</summary>
public sealed class NullKeyProvider : IEncryptionKeyProvider
{
    public string Source => EncryptionData.NoneEncryptedSource;

    public bool IsForSource(string source) => string.IsNullOrWhiteSpace(source) || Source.Equals(source, StringComparison.Ordinal);

    public Passphrase GetPassphrase(EncryptionData encryptionData) => new(Source);

    public static IEncryptionKeyResolver Resolver(InfrastructureOptions options) =>
        new EncryptionKeyResolver(new EncryptionState(SqliteConnectionFactory.BankPathFor(options)), [new NoneEncryptionKeyProvider()]);
}
