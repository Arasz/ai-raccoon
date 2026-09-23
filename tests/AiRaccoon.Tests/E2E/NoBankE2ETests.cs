using System.CommandLine;
using System.Globalization;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     F39 end to end (ADR-0106 D4) on the built binary: a mistyped --data-root must never become a
///     bank. Every verb that auto-launches a backend — the bare proxy, and every leaf of the command
///     tree routed through the settings server — exits 22 at a typo root and leaves it exactly as it
///     found it. The verb list is derived from the command tree itself, so a verb added later is
///     covered without anyone remembering to list it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Trait(TestCategories.Retry, TestCategories.Never)]
[Collection(E2ETestCollection.Name)]
public sealed class NoBankE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);

    /// <summary>The verbs that are not auto-launch paths by ruling: they become or inspect the bank themselves (D4).</summary>
    private static readonly string[] SelfContainedVerbs = ["serve", "doctor"];

    private readonly string _parent = TestData.CreateTempRoot("no-bank-typo");
    private IAsyncDisposable? _env;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _env = await EnvScope.AcquireAsync(Ct, (EnvEncryptionKeyProvider.EnvVarName, null));

    public async ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_parent);
        if (_env is not null)
        {
            await _env.DisposeAsync();
        }
    }

    [Fact]
    public async Task EveryAutoLaunchVerb_AtATypoRoot_ExitsNoBankAndLeavesTheDirectoryEmpty()
    {
        var verbs = AutoLaunchVerbs();
        // The derivation must not silently shrink: every routed top-level verb contributes a runnable leaf.
        var families = CliCommandTree.BuildFullRootCommand().Subcommands.Select(command => command.Name)
            .Where(name => !SelfContainedVerbs.Contains(name) && !CliWriteOptOuts.WritesDirectly([name]));
        families.Except(verbs.Select(verb => verb.FirstOrDefault())).ShouldBeEmpty("every routed verb family must contribute a runnable leaf");

        var typo = Path.Combine(_parent, "typo");
        Directory.CreateDirectory(typo);
        var missing = Path.Combine(_parent, "never-created");
        var violations = new List<string>();
        foreach (var verb in verbs)
        {
            foreach (var root in new[] { typo, missing })
            {
                var port = FreePort();
                try
                {
                    var shown = verb.Length == 0 ? "(bare launch)" : string.Join(' ', verb);
                    ProcessRun run;
                    try
                    {
                        run = await RaccoonProcess.RunWithClosedInputAsync(
                            ["--data-root", root, "--port", port.ToString(CultureInfo.InvariantCulture), .. verb], HardCap, Ct);
                    }
                    catch (TimeoutException)
                    {
                        run = new ProcessRun(-1, "", "it did not exit: it launched instead of refusing");
                    }

                    if (run.ExitCode != ErrorCode.Bank.NoBank)
                    {
                        violations.Add($"'{shown}' at {Path.GetFileName(root)} exited {run.ExitCode}: {run.Stderr.Trim()}");
                    }

                    if (root == typo && Directory.EnumerateFileSystemEntries(typo).Any())
                    {
                        violations.Add($"'{shown}' left [{string.Join(", ", Directory.EnumerateFileSystemEntries(typo).Select(Path.GetFileName))}] under the typo root");
                    }

                    if (root == missing && Directory.Exists(missing))
                    {
                        violations.Add($"'{shown}' created the missing root");
                    }
                }
                finally
                {
                    await RealServe.StopByPortAsync(port, TestData.CreateInfrastructureOptions(root));
                    EmptyOut(typo);
                    if (Directory.Exists(missing))
                    {
                        TestData.DeleteTempRoot(missing);
                    }
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    /// <summary>
    ///     The bare launch plus every leaf of the command tree that routes through the settings server
    ///     and needs no argument to run: everything but the self-contained verbs and the verbs that write
    ///     the bank directly (<see cref="CliWriteOptOuts" />).
    /// </summary>
    private static List<string[]> AutoLaunchVerbs()
    {
        var verbs = new List<string[]> { Array.Empty<string>() };
        foreach (var path in Leaves(CliCommandTree.BuildFullRootCommand(), []))
        {
            if (SelfContainedVerbs.Contains(path[0]) || CliWriteOptOuts.WritesDirectly(path))
            {
                continue;
            }

            verbs.Add(path);
        }

        return verbs;
    }

    private static IEnumerable<string[]> Leaves(Command command, string[] path)
    {
        if (command.Subcommands.Count == 0)
        {
            if (command.Arguments.All(argument => argument.Arity.MinimumNumberOfValues == 0)
                && command.Options.All(option => !option.Required))
            {
                yield return path;
            }

            yield break;
        }

        foreach (var subcommand in command.Subcommands)
        {
            foreach (var leaf in Leaves(subcommand, [.. path, subcommand.Name]))
            {
                yield return leaf;
            }
        }
    }

    private static void EmptyOut(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static int FreePort()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        return port;
    }
}
