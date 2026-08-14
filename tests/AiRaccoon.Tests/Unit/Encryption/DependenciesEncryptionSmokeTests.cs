using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using DotNext.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     Composition-root smoke: the provider family (none/env/bitwarden) is registered behind the resolver,
///     and the resolver reads the env passphrase when no sidecar exists (docs/plans/encryption-bitwarden-implementation.md S2b).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DependenciesEncryptionSmokeTests
{
    [Fact]
    public void RegisterMemoryServices_WiresEncryptionProviderFamily()
    {
        var tempRoot = TestData.CreateTempRoot();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.RegisterMemoryServices(new InfrastructureOptions { DataRoot = tempRoot, Scope = InstallScope.User }, IReadOnlyList<McpTransport>.Singleton(McpTransport.Http));

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IEncryptionKeyResolver>().ShouldNotBeNull();
            provider.GetServices<IEncryptionKeyProvider>().Count().ShouldBe(3);
            provider.GetRequiredService<ICliSecretManager>().ShouldNotBeNull();
            provider.GetRequiredService<SqliteConnectionFactory>().ShouldNotBeNull();
            provider.GetRequiredService<IHttpClientFactory>().ShouldNotBeNull();
        }
        finally
        {
            TestData.DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task RegisterMemoryServices_ResolverReadsEnvPassphraseWhenNoSidecar()
    {
        var tempRoot = TestData.CreateTempRoot();
        try
        {
            await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
                (EnvEncryptionKeyProvider.EnvVarName, "smoke-pass"));
            var services = new ServiceCollection();
            services.AddLogging();
            services.RegisterMemoryServices(new InfrastructureOptions { DataRoot = tempRoot, Scope = InstallScope.User }, IReadOnlyList<McpTransport>.Singleton(McpTransport.Http));

            using var provider = services.BuildServiceProvider();

            (await provider.GetRequiredService<IEncryptionKeyResolver>()
                .ResolveAsync(TestContext.Current.CancellationToken)).Passphrase.ShouldBe("smoke-pass");
        }
        finally
        {
            TestData.DeleteTempRoot(tempRoot);
        }
    }
}
