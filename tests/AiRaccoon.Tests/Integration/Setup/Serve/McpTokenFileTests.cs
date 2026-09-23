using AiRaccoon.Hosting.Common;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     Token-file acceptance (docs/plans/2026-08-09-mcp-loopback-token-flow.md): exclusive 0600
///     mint, reuse across restarts, convergence when two mints race, a reader that treats a
///     missing, empty or unreadable file as absent, and the self-heal for debris left by a crash.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class McpTokenFileTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-mcp-token");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task TokenFile_IsCreated_0600()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("UnixFileMode is POSIX-only; on Windows the file inherits the data-root ACL");
        }
        else
        {
            var tokenFile = new McpTokenFile(_dataRoot);

            await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

            File.GetUnixFileMode(tokenFile.Path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [RetryFact]
    public async Task ARestart_KeepsTheExistingToken()
    {
        var first = await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);

        var second = await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);

        second.ShouldBe(first);
    }

    [RetryFact]
    public async Task TwoConcurrentMints_ConvergeOnOneToken()
    {
        // The create is raced directly: an EnsureAsync fan-out lets the first minter finish before
        // the others look, so its read-first shortcut hides the collision this test exists for.
        const int minters = 16;
        var minted = new string?[minters];

        RaceOnThreads(minters, index =>
            minted[index] = new McpTokenFile(_dataRoot).TryMintAsync(CancellationToken.None).GetAwaiter().GetResult());

        var winner = minted.Where(token => token is not null).ShouldHaveSingleItem();
        var tokens = await Task.WhenAll(Enumerable.Range(0, minters)
            .Select(_ => new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)));
        tokens.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem().ShouldBe(winner);
        (await File.ReadAllTextAsync(new McpTokenFile(_dataRoot).Path, TestContext.Current.CancellationToken)).ShouldBe(winner);
    }

    /// <summary>The exclusive-create half of convergence, without the timing: a mint against an
    /// existing file must leave that file alone.</summary>
    [RetryFact]
    public async Task AMintAgainstAnExistingFile_LeavesTheStoredTokenUntouched()
    {
        var tokenFile = new McpTokenFile(_dataRoot);
        var first = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        (await tokenFile.TryMintAsync(TestContext.Current.CancellationToken)).ShouldBeNull();

        tokenFile.Read().ShouldBe(first);
    }

    [RetryFact]
    public async Task AMintedToken_IsAUrlSafe256BitSecret()
    {
        var token = await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldNotBeNull().ShouldMatch("^[A-Za-z0-9_-]{43}$"); // 32 bytes, base64url, unpadded
    }

    [RetryFact]
    public async Task Read_ReturnsTheMintedToken()
    {
        var minted = await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);

        new McpTokenFile(_dataRoot).Read().ShouldBe(minted);
    }

    [RetryFact]
    public void Read_TreatsAMissingFileAsAbsent() => new McpTokenFile(_dataRoot).Read().ShouldBeNull();

    [RetryFact]
    public async Task Read_TreatsAnEmptyFileAsAbsent_AndNeverMints()
    {
        var tokenFile = new McpTokenFile(_dataRoot);
        await WriteOwnerOnlyAsync(tokenFile.Path, "   ");

        tokenFile.Read().ShouldBeNull();
        (await File.ReadAllTextAsync(tokenFile.Path, TestContext.Current.CancellationToken)).ShouldBe("   ");
    }

    [RetryFact]
    public async Task Read_TrimsTheTrailingNewlineAnEditorMayLeave()
    {
        var tokenFile = new McpTokenFile(_dataRoot);
        var stored = await MintElsewhereAsync();
        await WriteOwnerOnlyAsync(tokenFile.Path, $"{stored}{Environment.NewLine}");

        tokenFile.Read().ShouldBe(stored);
    }

    [RetryFact]
    public async Task AMalformedTokenFile_IsTreatedAsAbsent()
    {
        // A write cut short leaves a prefix of the secret: fewer bits than minted, and accepting it
        // would be the same silent entropy loss whether the file is short by one character or forty.
        var tokenFile = new McpTokenFile(_dataRoot);
        var truncated = (await MintElsewhereAsync())[..10];
        await WriteOwnerOnlyAsync(tokenFile.Path, truncated);

        tokenFile.Read().ShouldBeNull();
        (await File.ReadAllTextAsync(tokenFile.Path, TestContext.Current.CancellationToken)).ShouldBe(truncated);
    }

    /// <summary>The length the reader demands is the mint's own output length, not a copy of it.</summary>
    [RetryFact]
    public async Task TheAcceptedLength_FollowsWhatTheMintProduces()
    {
        var tokenFile = new McpTokenFile(_dataRoot);
        var minted = (await tokenFile.EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();

        tokenFile.Read().ShouldBe(minted);

        await WriteOwnerOnlyAsync(tokenFile.Path, minted[..^1]);
        tokenFile.Read().ShouldBeNull(); // one character short of a mint is not a token
    }

    [RetryFact]
    public async Task AnEmptyTokenFile_IsHealed_AfterTheWait()
    {
        // Debris from a crash between the exclusive create and the write: wedging every later
        // start on it is worse than minting a new secret nobody is holding.
        var time = new FakeTimeProvider();
        var tokenFile = new McpTokenFile(_dataRoot, time);
        await WriteOwnerOnlyAsync(tokenFile.Path, string.Empty);
        AgeTheDebris(tokenFile.Path, time);

        var healing = tokenFile.EnsureAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken); // timer registers
        healing.IsCompleted.ShouldBeFalse(); // still waiting out a possibly-live writer

        time.Advance(McpTokenFile.HealAfter);
        var healed = await healing.WaitAsync(TestContext.Current.CancellationToken);

        healed.ShouldNotBeNullOrWhiteSpace();
        tokenFile.Read().ShouldBe(healed);
    }

    /// <summary>F7: the delete only fires once the debris is older than the heal window.</summary>
    [RetryFact]
    public async Task AnEmptyTokenFile_YoungerThanTheHealWindow_IsNotDeleted()
    {
        var time = new FakeTimeProvider();
        var tokenFile = new McpTokenFile(_dataRoot, time);
        await WriteOwnerOnlyAsync(tokenFile.Path, string.Empty);
        File.SetLastWriteTimeUtc(tokenFile.Path, time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(5));

        var healing = tokenFile.EnsureAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken); // timer registers
        time.Advance(McpTokenFile.HealAfter);

        var healed = await healing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        healed.ShouldBeNull();
        File.Exists(tokenFile.Path).ShouldBeTrue("the debris is younger than the window, so it is left alone");
    }

    [RetryFact]
    public async Task AMalformedTokenFile_IsHealed()
    {
        // A write cut short by a crash leaves a prefix of the secret behind. Serving on it lowers
        // the entropy silently, and wedging every later start on it is no better than for an empty
        // file: it gets the same wait, then the same delete-and-re-mint.
        var time = new FakeTimeProvider();
        var tokenFile = new McpTokenFile(_dataRoot, time);
        var truncated = (await MintElsewhereAsync())[..10];
        await WriteOwnerOnlyAsync(tokenFile.Path, truncated);
        AgeTheDebris(tokenFile.Path, time);

        var healing = tokenFile.EnsureAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken); // timer registers
        healing.IsCompleted.ShouldBeFalse(); // still waiting out a possibly-live writer

        time.Advance(McpTokenFile.HealAfter);
        var healed = await healing.WaitAsync(TestContext.Current.CancellationToken);

        healed.ShouldNotBeNull().ShouldNotBe(truncated);
        tokenFile.Read().ShouldBe(healed);
    }

    [RetryFact]
    public async Task AnEmptyFileThatFillsIn_IsNotOverwritten()
    {
        // The creator was alive after all. Clobbering it here is worse than the wedge: the writer
        // goes on to serve with its own secret while the file holds ours, so every caller 401s.
        var time = new FakeTimeProvider();
        var tokenFile = new McpTokenFile(_dataRoot, time);
        var writers = await MintElsewhereAsync();
        await WriteOwnerOnlyAsync(tokenFile.Path, string.Empty);
        File.SetLastWriteTimeUtc(tokenFile.Path, time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(5));

        var healing = tokenFile.EnsureAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken); // timer registers
        await WriteOwnerOnlyAsync(tokenFile.Path, writers);
        time.Advance(McpTokenFile.HealAfter);

        var acquired = await healing.WaitAsync(TestContext.Current.CancellationToken);

        acquired.ShouldBe(writers);
        tokenFile.Read().ShouldBe(writers);
    }

    /// <summary>The clobber guard without the timing: the heal's delete only fires while the file
    /// holds no token.</summary>
    [RetryFact]
    public async Task TheHealDelete_LeavesAStoredTokenAlone()
    {
        var tokenFile = new McpTokenFile(_dataRoot);
        var existing = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        tokenFile.TryDeleteDebris().ShouldBeFalse();

        File.Exists(tokenFile.Path).ShouldBeTrue();
        tokenFile.Read().ShouldBe(existing);
    }

    [RetryFact]
    public async Task TwoProcessesHealingConcurrently_ConvergeOnOneToken()
    {
        // Healing routes back through the exclusive create rather than writing directly, so racing
        // healers converge exactly as racing minters do.
        const int healers = 16;
        var healAfter = TimeSpan.FromMilliseconds(200);
        await WriteOwnerOnlyAsync(new McpTokenFile(_dataRoot).Path, string.Empty);
        File.SetLastWriteTimeUtc(new McpTokenFile(_dataRoot).Path, DateTime.UtcNow - healAfter - TimeSpan.FromSeconds(1));
        var healed = new string?[healers];

        RaceOnThreads(healers, index =>
            healed[index] = new McpTokenFile(_dataRoot, healAfter: healAfter)
                .EnsureAsync(CancellationToken.None).GetAwaiter().GetResult());

        var token = healed.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem();
        token.ShouldNotBeNullOrWhiteSpace();
        (await File.ReadAllTextAsync(new McpTokenFile(_dataRoot).Path, TestContext.Current.CancellationToken)).ShouldBe(token);
    }

    [RetryFact]
    public async Task ATokenPathThatCannotBeHealed_ReportsFailureInsteadOfThrowing()
    {
        // A directory where the file belongs: unreadable, uncreatable and undeletable.
        var tokenFile = new McpTokenFile(_dataRoot, healAfter: TimeSpan.FromMilliseconds(50));
        Directory.CreateDirectory(tokenFile.Path);

        var token = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldBeNull();
    }

    /// <summary>A well-formed token from a mint of its own — what a live writer leaves behind, and
    /// the only honest source of the shape the reader accepts.</summary>
    private async Task<string> MintElsewhereAsync()
    {
        var elsewhere = Path.Combine(_dataRoot, Guid.NewGuid().ToString("N"));
        return (await new McpTokenFile(elsewhere).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    /// <summary>Backdates debris to before the fake clock's heal window, where a real crash would leave it.</summary>
    private static void AgeTheDebris(string path, FakeTimeProvider time) =>
        File.SetLastWriteTimeUtc(path, time.GetUtcNow().UtcDateTime - McpTokenFile.HealAfter - TimeSpan.FromSeconds(1));

    /// <summary>Seeds a hand-written token file: the reader refuses a shared file, so tests set 0600 first.</summary>
    private static async Task WriteOwnerOnlyAsync(string path, string content)
    {
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

    [RetryFact]
    public async Task EnsureAsync_CreatesTheDataRoot_WhenItDoesNotExistYet()
    {
        var nested = Path.Combine(_dataRoot, "not-created-yet");

        var token = await new McpTokenFile(nested).EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldNotBeNullOrWhiteSpace();
        new McpTokenFile(nested).Read().ShouldBe(token);
    }
}
