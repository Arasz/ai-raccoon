using AiRaccoon.Setup.Serve;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     Token-file acceptance (docs/plans/2026-08-09-mcp-loopback-token-flow.md): exclusive 0600
///     mint, reuse across restarts, convergence when two mints race, and a reader that treats a
///     missing, empty or unreadable file as absent.
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

        token.ShouldMatch("^[A-Za-z0-9_-]{43}$"); // 32 bytes, base64url, unpadded
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
