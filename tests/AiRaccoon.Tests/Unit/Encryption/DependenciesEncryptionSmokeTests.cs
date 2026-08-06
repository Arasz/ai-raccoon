using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     Composition-root smoke: the provider family (none/env/bitwarden) is registered behind
///     the resolver, and the resolver reads the env passphrase when no sidecar exists (plan §S2b wiring).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DependenciesEncryptionSmokeTests
{
    [Fact]
    public void RegisterMemoryServices_WiresEncryptionProviderFamily()
    {
        var tempRoot = TestData.CreateTempRoot("ai-raccoon-tests");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.RegisterMemoryServices(new InfrastructureOptions { DataRoot = tempRoot, Scope = InstallScope.User });

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IEncryptionKeyResolver>().ShouldNotBeNull();
            provider.GetServices<IEncryptionKeyProvider>().Count().ShouldBe(3);
            provider.GetRequiredService<ICliSecretManager>().ShouldNotBeNull();
            provider.GetRequiredService<SqliteConnectionFactory>().ShouldNotBeNull();
            // BundledModel resolves an IHttpClientFactory — without the registration the
            // server boot fails with "Unable to resolve service for IHttpClientFactory".
            provider.GetRequiredService<IHttpClientFactory>().ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void RegisterMemoryServices_ResolverReadsEnvPassphraseWhenNoSidecar()
    {
        var tempRoot = TestData.CreateTempRoot("ai-raccoon-tests");
        TestData.EnvVarGate.Wait(TestContext.Current.CancellationToken);
        try
        {
            var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
            try
            {
                Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, "smoke-pass");
                var services = new ServiceCollection();
                services.AddLogging();
                services.RegisterMemoryServices(new InfrastructureOptions { DataRoot = tempRoot, Scope = InstallScope.User });

                using var provider = services.BuildServiceProvider();

                provider.GetRequiredService<IEncryptionKeyResolver>().Resolve().Passphrase.ShouldBe("smoke-pass");
            }
            finally
            {
                Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
            }
        }
        finally
        {
            TestData.EnvVarGate.Release();
            Directory.Delete(tempRoot, true);
        }
    }
}
