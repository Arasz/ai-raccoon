using AiRaccoon.Hosting.Common;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     Races on the owner-only secret files: racing mints, heals and legacy adoptions converge on
///     one secret, a writer that fills its file in during the heal wait is never clobbered, and the
///     identity key and the MCP token kept in one state directory never touch each other's file.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SecretFileRaceTests : IDisposable
{
    private const int Racers = 16;

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-secret-race");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task RacingKeyMints_ExactlyOneWins_AndEveryEnsureReturnsIt()
    {
        var minted = new string?[Racers];
        OwnerOnlyFile.EnsureDirectory(NewKeyFile().StateDirectory);

        RaceOnThreads(Racers, index =>
            minted[index] = NewKeyFile().TryMintAsync(CancellationToken.None).GetAwaiter().GetResult() is { } key
                ? IdentityProof.KeyId(key)
                : null);

        var winner = minted.Where(id => id is not null).ShouldHaveSingleItem();
        RaceOnThreads(Racers, index =>
            minted[index] = NewKeyFile().TryMintAsync(CancellationToken.None).GetAwaiter().GetResult() is { } key
                ? IdentityProof.KeyId(key)
                : null);
        minted.ShouldAllBe(id => id == null, "a mint against an existing key file must lose");
        var ensured = await Task.WhenAll(Enumerable.Range(0, Racers)
            .Select(_ => NewKeyFile().EnsureAsync(TestContext.Current.CancellationToken)));
        ensured.Select(key => IdentityProof.KeyId(key.ShouldNotBeNull())).Distinct(StringComparer.Ordinal)
            .ShouldHaveSingleItem().ShouldBe(winner);
    }

    [RetryFact]
    public async Task RacingKeyHealers_ConvergeOnOneKey()
    {
        var healAfter = TimeSpan.FromMilliseconds(200);
        var keyPath = NewKeyFile().Path;
        await SeedOwnerOnlyAsync(keyPath, string.Empty);
        File.SetLastWriteTimeUtc(keyPath, DateTime.UtcNow - healAfter - TimeSpan.FromSeconds(1));
        var healed = new string?[Racers];

        RaceOnThreads(Racers, index =>
            healed[index] = new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot), healAfter: healAfter)
                .EnsureAsync(CancellationToken.None).GetAwaiter().GetResult() is { } key
                ? IdentityProof.KeyId(key)
                : null);

        var keyId = healed.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem();
        keyId.ShouldNotBeNull();
        NewKeyFile().ReadKeyId().ShouldBe(keyId);
    }

    [RetryFact]
    public async Task AKeyFileThatFillsInDuringTheHealWait_IsNotOverwritten()
    {
        var time = new FakeTimeProvider();
        var keyFile = new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot), time);
        var writers = await MintKeyElsewhereAsync();
        await SeedOwnerOnlyAsync(keyFile.Path, string.Empty);
        File.SetLastWriteTimeUtc(keyFile.Path, time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(5));

        var healing = keyFile.EnsureAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken); // timer registers
        await File.WriteAllTextAsync(keyFile.Path, writers.Pem, TestContext.Current.CancellationToken);
        time.Advance(IdentityKeyFile.HealAfter);

        var acquired = await healing.WaitAsync(TestContext.Current.CancellationToken);

        IdentityProof.KeyId(acquired.ShouldNotBeNull()).ShouldBe(writers.KeyId);
        (await File.ReadAllTextAsync(keyFile.Path, TestContext.Current.CancellationToken)).ShouldBe(writers.Pem);
    }

    [RetryFact]
    public async Task RacingLegacyAdoptions_ConvergeOnTheLegacyToken_AndDeleteIt()
    {
        var legacyToken = new string('A', 43);
        var legacyPath = Path.Combine(_dataRoot, McpTokenFile.FileName);
        await SeedOwnerOnlyAsync(legacyPath, legacyToken);
        var adopted = new string?[Racers];

        RaceOnThreads(Racers, index =>
            adopted[index] = NewTokenFile().EnsureAsync(CancellationToken.None).GetAwaiter().GetResult());

        adopted.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem().ShouldBe(legacyToken);
        NewTokenFile().Read().ShouldBe(legacyToken);
        File.Exists(legacyPath).ShouldBeFalse("the legacy file goes once the state-dir write landed");
    }

    [RetryFact]
    public async Task KeyAndTokenEnsuredTogether_EachConvergeOnTheirOwnFile()
    {
        var keyIds = new string?[Racers];
        var tokens = new string?[Racers];

        RaceOnThreads(Racers * 2, index =>
        {
            if (index % 2 == 0)
            {
                keyIds[index / 2] = NewKeyFile().EnsureAsync(CancellationToken.None).GetAwaiter().GetResult() is { } key
                    ? IdentityProof.KeyId(key)
                    : null;
            }
            else
            {
                tokens[index / 2] = NewTokenFile().EnsureAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        });

        var keyId = keyIds.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem().ShouldNotBeNull();
        var token = tokens.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem().ShouldNotBeNull();
        NewKeyFile().ReadKeyId().ShouldBe(keyId);
        NewTokenFile().Read().ShouldBe(token);
        (await File.ReadAllTextAsync(NewKeyFile().Path, TestContext.Current.CancellationToken)).ShouldNotContain(token);
    }

    [RetryFact]
    public async Task AKeyEnsure_WaitsForAnotherProcessHoldingTheLock() =>
        await EnsureWaitsForTheLockAsync(NewKeyFile().Path, async () => await NewKeyFile().EnsureAsync(CancellationToken.None));

    [RetryFact]
    public async Task ATokenEnsure_WaitsForAnotherProcessHoldingTheLock() =>
        await EnsureWaitsForTheLockAsync(NewTokenFile().Path, async () => await NewTokenFile().EnsureAsync(CancellationToken.None));

    [RetryFact]
    public async Task TheHealDelete_LeavesAnAgedKeyAlone()
    {
        var keyFile = NewKeyFile();
        var keyId = IdentityProof.KeyId((await keyFile.EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull());
        File.SetLastWriteTimeUtc(keyFile.Path, DateTime.UtcNow - TimeSpan.FromHours(1));

        keyFile.TryDeleteDebris().ShouldBeFalse();

        NewKeyFile().ReadKeyId().ShouldBe(keyId);
    }

    [RetryFact]
    public async Task TheHealDelete_LeavesAnAgedTokenAlone()
    {
        var tokenFile = NewTokenFile();
        var token = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(tokenFile.Path, DateTime.UtcNow - TimeSpan.FromHours(1));

        tokenFile.TryDeleteDebris().ShouldBeFalse();

        NewTokenFile().Read().ShouldNotBeNull().ShouldBe(token);
    }

    /// <summary>An ensure started while another process holds the lock file finishes only after it is released.</summary>
    private static async Task EnsureWaitsForTheLockAsync(string secretPath, Func<Task<object?>> ensure)
    {
        OwnerOnlyFile.EnsureDirectory(Path.GetDirectoryName(secretPath)!);
        var held = await OwnerOnlyFile.AcquireLockAsync(secretPath, TimeProvider.System, TestContext.Current.CancellationToken);
        Task<object?> ensuring;
        await using (held)
        {
            ensuring = Task.Run(ensure, TestContext.Current.CancellationToken);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            ensuring.IsCompleted.ShouldBeFalse("the lock holder may be mid-mint");
        }

        (await ensuring.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    private IdentityKeyFile NewKeyFile() => new(TestData.CreateProjectOptions(_dataRoot));

    private McpTokenFile NewTokenFile() => new(TestData.CreateProjectOptions(_dataRoot));

    /// <summary>A valid key from a mint of its own: what a live writer leaves behind.</summary>
    private async Task<(string Pem, string KeyId)> MintKeyElsewhereAsync()
    {
        var elsewhere = new IdentityKeyFile(TestData.CreateProjectOptions(Path.Combine(_dataRoot, "elsewhere")));
        var key = (await elsewhere.EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        return (await File.ReadAllTextAsync(elsewhere.Path, TestContext.Current.CancellationToken), IdentityProof.KeyId(key));
    }

    /// <summary>Seeds a hand-written secret file inside an owner-only directory, as the readers require.</summary>
    private static async Task SeedOwnerOnlyAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.GetDirectoryName(path)!,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Runs the action on dedicated threads released together by a barrier.</summary>
    private static void RaceOnThreads(int count, Action<int> action)
    {
        using var barrier = new Barrier(count);
        var threads = Enumerable.Range(0, count)
            .Select(index => new Thread(b =>
            {
                (b as Barrier)?.SignalAndWait();
                action(index);
            }))
            .ToArray();

        foreach (var thread in threads)
        {
            thread.Start(barrier);
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }
    }
}
