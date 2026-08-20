using System.Collections;
using System.Reflection;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli.Commands;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     The gate `derive-or-delete-the-list` asks for: <see cref="CliWriteOptOuts" /> only ever pinned
///     the <em>--apply</em> leaves a human remembered to add a row for — <c>repair reingest --apply</c>
///     and <c>repair chunk-index --apply</c> both wrote the bank directly for months without ever
///     being added to it, and <c>noise entries</c> / <c>watch registered</c> did the same thing on
///     the read side, because nothing compared the opt-out list against what the CLI's DI graph
///     actually constructs.
///     <para />
///     This asserts the DI graph itself instead of a maintained list of command names: every command
///     type <see cref="ConfigCommands" />'s own constructor depends on (the command registration,
///     derived by reflection rather than hand-listed) is resolved from the exact service graph
///     <c>AppRunner.RunCliCommand</c> builds for a non-opted-out command path, then walked field by
///     field for a live <see cref="ISqliteConnectionFactory" /> — the one seam every bank-opening
///     type in this codebase holds, directly or transitively. A command whose graph reaches one opens
///     the bank; a command routed through <see cref="LazyServerSettingsStore" /> never does, because
///     that store holds a deferred acquire delegate (skipped — see <see cref="FindLiveBankConnectionFactory" />)
///     and, until first use, nothing else.
///     <para />
///     <see cref="ConfigCommands" /> itself is excluded: it is the dispatcher, not a leaf command —
///     detected structurally as "the one command type whose constructor depends on other command
///     types" rather than named — and its own <c>IMemoryStore</c> dependency exists only to hand
///     settings-shaped calls through to the leaves below, which is exactly the seam
///     <c>SqliteMemoryStore</c> forwards to the injected <c>ISettingsStore</c> for (never opening the
///     bank itself for those calls). Three sanctioned exceptions —
///     <see cref="EncryptionCommands" /> (bootstrap: it keys the bank before a server can decrypt it),
///     <see cref="DoctorCommands" /> (its own read-only connection, deliberately bypassing
///     <c>EnsureAsync</c>, <c>DoctorCommands.cs:85-102</c>), and <see cref="ServeCommands" /> (running
///     it <em>is</em> becoming the server, not asking one to do something — <c>NodeRunner</c> probes
///     the bank's own decryption key before it binds the port, <c>NodeRunner.cs:107</c>) — are named
///     in <see cref="BankCapableCliCommandAllowlist" />, kept beside <see cref="CliWriteOptOuts" /> so
///     both lists are amended together. The third one was not anticipated going in — this test's own
///     first red run found it, which is the point of deriving from the constructed graph rather than
///     trusting a hand-written list to already be complete. That allowlist is still hand-maintained —
///     the honest admission this class's own doc comment makes — but it is now three names long
///     instead of a list nothing ever checked against the code.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliCommandsDoNotOpenTheBankTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-cli-bank-capable");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void NoLeafCommandType_ReachesALiveSqliteConnectionFactory()
    {
        var leafCommandTypes = DeriveLeafCommandTypesFromTheCommandRegistration();

        // Sanity bound on the derivation itself: if this ever drops to near-zero, the reflection
        // below silently stopped finding anything and every other assertion in this test would
        // pass for the wrong reason (nothing was checked). Loose on purpose — it must not need an
        // edit every time a command is added or removed.
        leafCommandTypes.Length.ShouldBeGreaterThanOrEqualTo(8);

        using var provider = BuildCliServiceProvider();

        var violations = new List<string>();
        foreach (var commandType in leafCommandTypes)
        {
            if (BankCapableCliCommandAllowlist.Types.Contains(commandType))
            {
                continue;
            }

            var instance = provider.GetRequiredService(commandType);
            var path = FindLiveBankConnectionFactory(instance);
            if (path is not null)
            {
                violations.Add($"{commandType.Name} reaches a live ISqliteConnectionFactory via {path}");
            }
        }

        violations.ShouldBeEmpty(
            $"the CLI must never construct a command holding a live bank connection outside " +
            $"{nameof(BankCapableCliCommandAllowlist)}; offenders:\n{string.Join('\n', violations)}");
    }

    /// <summary>
    ///     "The command registration" is <see cref="ConfigCommands" />'s own constructor: every
    ///     parameter whose type name ends in "Commands" is a command the dispatcher was handed by
    ///     <see cref="AiRaccoon.Setup.Cli.Commands.CommandsRegistration.RegisterCommands" />.
    ///     <see cref="ConfigCommands" /> is not itself in that list — nothing depends on it — which is
    ///     what makes "depends on another *Commands type" the structural signal that excludes the
    ///     dispatcher from its own leaf-command check without naming it.
    /// </summary>
    private static Type[] DeriveLeafCommandTypesFromTheCommandRegistration()
    {
        var configCommandsConstructor = typeof(ConfigCommands)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();

        return
        [
            .. configCommandsConstructor.GetParameters()
                .Select(p => p.ParameterType)
                .Where(t => t.Name.EndsWith("Commands", StringComparison.Ordinal))
        ];
    }

    /// <summary>Exactly the graph AppRunner.RunCliCommand builds for a command path CliWriteOptOuts does not opt out — RegisterCoreMemoryServices + RegisterCommands, then the same override the CLI applies. The acquire delegate here must never run: the walk below stops at delegates rather than invoking them.</summary>
    private ServiceProvider BuildCliServiceProvider()
    {
        var services = new ServiceCollection();
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        services.AddLogging();
        services.RegisterCoreMemoryServices(options);
        services.RegisterCommands();

        var lazyServerStore = new LazyServerSettingsStore(_ =>
            throw new InvalidOperationException(
                "ai-raccoon: the acquire delegate must never run while this test only constructs commands"));
        services.AddSingleton<ISettingsStore>(lazyServerStore);
        services.AddSingleton<IModelMigrationStore>(lazyServerStore);
        services.AddSingleton<IRepairStore>(lazyServerStore);
        services.AddSingleton<IPromotionQueuePruneStore>(lazyServerStore);
        services.AddSingleton<IMaintenanceStatsStore>(lazyServerStore);
        services.AddSingleton<INoiseSummaryStore>(lazyServerStore);
        services.AddSingleton<IWatchRegisteredStore>(lazyServerStore);

        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     Walks the constructed object graph for a live <see cref="ISqliteConnectionFactory" /> —
    ///     the one seam every bank-opening type in this codebase holds. Bounded to AiRaccoon.* types
    ///     (BCL/third-party instances never hold one reachable from a command's own fields) and never
    ///     descends into a delegate (LazyServerSettingsStore's deferred acquire closure above all —
    ///     it must not be invoked, and its captured state is not part of the constructed graph
    ///     anyway). Returns the dotted field path to the first factory found, or null.
    /// </summary>
    private static string? FindLiveBankConnectionFactory(object root)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Walk(root, root.GetType().Name, 0);

        string? Walk(object obj, string path, int depth)
        {
            if (depth > 8 || !visited.Add(obj))
            {
                return null;
            }

            if (obj is ISqliteConnectionFactory)
            {
                return path;
            }

            var type = obj.GetType();
            if (type.Namespace is null || !type.Namespace.StartsWith("AiRaccoon", StringComparison.Ordinal))
            {
                return null;
            }

            if (typeof(Delegate).IsAssignableFrom(type))
            {
                return null;
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? value;
                try
                {
                    value = field.GetValue(obj);
                }
                catch
                {
                    continue;
                }

                if (value is null)
                {
                    continue;
                }

                if (value is IEnumerable enumerable and not string)
                {
                    foreach (var item in enumerable)
                    {
                        if (item is null)
                        {
                            continue;
                        }

                        var foundInItem = Walk(item, $"{path}.{field.Name}[]", depth + 1);
                        if (foundInItem is not null)
                        {
                            return foundInItem;
                        }
                    }

                    continue;
                }

                var found = Walk(value, $"{path}.{field.Name}", depth + 1);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
