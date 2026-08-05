using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     Composition-root smoke: the resolver is the registered IEncryptionKeyProvider, with the
///     bws runner and factory resolving through it (plan §S2b wiring).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DependenciesEncryptionSmokeTests
{
    [Fact]
    public void RegisterMemoryServices_WiresResolverAsKeyProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions());

        using var provider = services.BuildServiceProvider();

        var resolver = provider.GetRequiredService<EncryptionKeyResolver>();
        provider.GetRequiredService<IEncryptionKeyProvider>().ShouldBeSameAs(resolver);
        provider.GetRequiredService<IBwsProcessRunner>().ShouldNotBeNull();
        provider.GetRequiredService<SqliteConnectionFactory>().ShouldNotBeNull();
    }
}
