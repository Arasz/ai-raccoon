using AiRaccoon.Setup.Serve;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     Token-file acceptance (docs/plans/2026-08-09-mcp-loopback-token-flow.md): exclusive 0600
///     mint, reuse across restarts, convergence when two mints race, a reader that treats a
///     missing, empty or unreadable file as absent, and the self-heal for debris left by a crash.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class McpTokenFileTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-mcp-token");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
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

    [Fact]
    public async Task ARestart_KeepsTheExistingToken()
    {
        var first = await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);

        var second = await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);

        second.ShouldBe(first);
    }

    [Fact]
    public async Task TwoConcurrentMints_ConvergeOnOneToken()
    {
        // The create is raced directly: an EnsureAsync fan-out lets the first minter finish before
        // the others look, so its read-first shortcut hides the collision this test exists for.
        const int Minters = 16;
        var minted = new string?[Minters];

        RaceOnThreads(Minters, index =>
            minted[index] = new McpTokenFile(_dataRoot).TryMintAsync(CancellationToken.None).GetAwaiter().GetResult());

        var winner = minted.Where(token => token is not null).ShouldHaveSingleItem();
        var tokens = await Task.WhenAll(Enumerable.Range(0, Minters)
            .Select(_ => new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)));
        tokens.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem().ShouldBe(winner);
        (await File.ReadAllTextAsync(new McpTokenFile(_dataRoot).Path, TestContext.Current.CancellationToken)).ShouldBe(winner);
    }

    /// <summary>The exclusive-create half of convergence, without the timing: a mint against an
    /// existing file must leave that file alone.</summary>
    [Fact]
    public async Task AMintAgainstAnExistingFile_LeavesTheStoredTokenUntouched()
    {
        var tokenFile = new McpTokenFile(_dataRoot);
        var first = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        (await tokenFile.TryMintAsync(TestContext.Current.CancellationToken)).ShouldBeNull();

        tokenFile.Read().ShouldBe(first);
    }

    [Fact]
    public async Task AMintedToken_IsAUrlSafe256BitSecret()
    {
        var token = await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldNotBeNull().ShouldMatch("^[A-Za-z0-9_-]{43}$"); // 32 bytes, base64url, unpadded
    }

    [Fact]
    public async Task Read_ReturnsTheMintedToken()
    {
        var minted = await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);

        new McpTokenFile(_dataRoot).Read().ShouldBe(minted);
    }

    [Fact]
    public void Read_TreatsAMissingFileAsAbsent() => new McpTokenFile(_dataRoot).Read().ShouldBeNull();

    [Fact]
    public async Task Read_TreatsAnEmptyFileAsAbsent_AndNeverMints()
    {
        var tokenFile = new McpTokenFile(_dataRoot);
        await File.WriteAllTextAsync(tokenFile.Path, "   ", TestContext.Current.CancellationToken);

        tokenFile.Read().ShouldBeNull();
        (await File.ReadAllTextAsync(tokenFile.Path, TestContext.Current.CancellationToken)).ShouldBe("   ");
    }

    [Fact]
    public async Task Read_TrimsTheTrailingNewlineAnEditorMayLeave()
    {
        var tokenFile = new McpTokenFile(_dataRoot);
        await File.WriteAllTextAsync(tokenFile.Path, $"s3cret{Environment.NewLine}", TestContext.Current.CancellationToken);

        tokenFile.Read().ShouldBe("s3cret");
    }

    [Fact]
    public async Task AnEmptyTokenFile_IsHealed_AfterTheWait()
    {
        // Debris from a crash between the exclusive create and the write: wedging every later
        // start on it is worse than minting a new secret nobody is holding.
        var time = new FakeTimeProvider();
        var tokenFile = new McpTokenFile(_dataRoot, time);
        await File.WriteAllTextAsync(tokenFile.Path, string.Empty, TestContext.Current.CancellationToken);

        var healing = tokenFile.EnsureAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken); // timer registers
        healing.IsCompleted.ShouldBeFalse(); // still waiting out a possibly-live writer

        time.Advance(McpTokenFile.HealAfter);
        var healed = await healing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        healed.ShouldNotBeNullOrWhiteSpace();
        tokenFile.Read().ShouldBe(healed);
    }

    [Fact]
    public async Task AnEmptyFileThatFillsIn_IsNotOverwritten()
    {
        // The creator was alive after all. Clobbering it here is worse than the wedge: the writer
        // goes on to serve with its own secret while the file holds ours, so every caller 401s.
        var time = new FakeTimeProvider();
        var tokenFile = new McpTokenFile(_dataRoot, time);
        await File.WriteAllTextAsync(tokenFile.Path, string.Empty, TestContext.Current.CancellationToken);

        var healing = tokenFile.EnsureAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken); // timer registers
        await File.WriteAllTextAsync(tokenFile.Path, "the-writers-token", TestContext.Current.CancellationToken);
        time.Advance(McpTokenFile.HealAfter);

        var acquired = await healing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        acquired.ShouldBe("the-writers-token");
        tokenFile.Read().ShouldBe("the-writers-token");
    }

    /// <summary>The clobber guard without the timing: the heal's delete only fires while the file
    /// is still empty.</summary>
    [Fact]
    public async Task TheHealDelete_LeavesAFilledFileAlone()
    {
        var tokenFile = new McpTokenFile(_dataRoot);
        var existing = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        tokenFile.TryDeleteWhileEmpty().ShouldBeFalse();

        File.Exists(tokenFile.Path).ShouldBeTrue();
        tokenFile.Read().ShouldBe(existing);
    }

    [Fact]
    public async Task TwoProcessesHealingConcurrently_ConvergeOnOneToken()
    {
        // Healing routes back through the exclusive create rather than writing directly, so racing
        // healers converge exactly as racing minters do.
        const int Healers = 16;
        var healAfter = TimeSpan.FromMilliseconds(200);
        await File.WriteAllTextAsync(new McpTokenFile(_dataRoot).Path, string.Empty, TestContext.Current.CancellationToken);
        var healed = new string?[Healers];

        RaceOnThreads(Healers, index =>
            healed[index] = new McpTokenFile(_dataRoot, healAfter: healAfter)
                .EnsureAsync(CancellationToken.None).GetAwaiter().GetResult());

        var token = healed.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem();
        token.ShouldNotBeNullOrWhiteSpace();
        (await File.ReadAllTextAsync(new McpTokenFile(_dataRoot).Path, TestContext.Current.CancellationToken)).ShouldBe(token);
    }

    [Fact]
    public async Task ATokenPathThatCannotBeHealed_ReportsFailureInsteadOfThrowing()
    {
        // A directory where the file belongs: unreadable, uncreatable and undeletable.
        var tokenFile = new McpTokenFile(_dataRoot, healAfter: TimeSpan.FromMilliseconds(50));
        Directory.CreateDirectory(tokenFile.Path);

        var token = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldBeNull();
    }

    /// <summary>Runs the action on dedicated threads released together by a barrier.</summary>
    private static void RaceOnThreads(int count, Action<int> action)
    {
        using var barrier = new Barrier(count);
        var threads = Enumerable.Range(0, count)
            .Select(index => new Thread(() =>
            {
                barrier.SignalAndWait();
                action(index);
            }))
            .ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }
    }

    [Fact]
    public async Task EnsureAsync_CreatesTheDataRoot_WhenItDoesNotExistYet()
    {
        var nested = Path.Combine(_dataRoot, "not-created-yet");

        var token = await new McpTokenFile(nested).EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldNotBeNullOrWhiteSpace();
        new McpTokenFile(nested).Read().ShouldBe(token);
    }
}
