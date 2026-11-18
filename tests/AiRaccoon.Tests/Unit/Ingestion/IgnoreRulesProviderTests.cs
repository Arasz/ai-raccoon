using AiRaccoon.Infrastructure.Ingestion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

/// <summary>
///     File-reading half of `ai-raccoon.ignore` (Core's IgnoreRules stays pure/no-I/O): reads
///     `&lt;root&gt;/ai-raccoon.ignore` and resolves to <c>IgnoreRules.Empty</c> when the file is
///     missing. The parsed rules are cached per root while the file's last write time and length
///     stay put (ADR-0151 — the digest asks on every filesystem event, so a per-event read was the
///     cost); a moved ignore file is re-read and re-parsed.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class IgnoreRulesProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ignore-rules-provider-tests", Guid.NewGuid().ToString("N"));
    private readonly IIgnoreRulesProvider _provider = new IgnoreRulesProvider();

    public IgnoreRulesProviderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task LoadAsync_NoIgnoreFile_ReturnsEmptyRules()
    {
        var rules = await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        rules.HasRules.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_IgnoreFilePresent_ParsesItsPatterns()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, IgnoreRulesProvider.FileName), "bin/\n*.generated.cs\n",
            TestContext.Current.CancellationToken);

        var rules = await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        rules.HasRules.ShouldBeTrue();
        rules.IsIgnored("bin/x.cs", isDirectory: false).ShouldBeTrue();
    }

    /// <summary>
    ///     Windows reports a watched file under the casing on disk, so the control file's own name must be
    ///     recognised there the way every other path comparison in this layer is (ADR-0151).
    /// </summary>
    [Fact]
    public void IsIgnoreFileName_FollowsTheHostOsCasing()
    {
        IgnoreRulesProvider.IsIgnoreFileName("ai-raccoon.ignore").ShouldBeTrue();
        IgnoreRulesProvider.IsIgnoreFileName("AI-RACCOON.IGNORE").ShouldBe(OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task LoadAsync_UnchangedIgnoreFile_ParsesOnceAndServesTheSameRules()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, IgnoreRulesProvider.FileName), "bin/\n",
            TestContext.Current.CancellationToken);

        var first = await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        var second = await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        second.ShouldBeSameAs(first,
            "an unmoved ignore file must not be re-read and re-parsed on every filesystem event (ADR-0151)");
    }

    [Fact]
    public async Task Invalidate_WhenTheFileMovedBackToTheSameStamp_ReadsTheFileAgain()
    {
        var file = Path.Combine(_root, IgnoreRulesProvider.FileName);
        await File.WriteAllTextAsync(file, "foo/\n", TestContext.Current.CancellationToken);
        var before = await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        // foo/ -> bar/ is the same length; restoring the timestamp models a filesystem whose stamp
        // cannot see the edit (or a rewrite that keeps it). The event that found the edit is what
        // makes the difference, so that is the signal the caller provides.
        var stamp = new FileInfo(file).LastWriteTimeUtc;
        await File.WriteAllTextAsync(file, "bar/\n", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(file, stamp);
        _provider.Invalidate(_root);

        var after = await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        after.ShouldNotBeSameAs(before, "an ignore-file event invalidates the cache, whatever the stamp still reads");
        after.IsIgnored("bar/x.cs", isDirectory: false).ShouldBeTrue();
        after.IsIgnored("foo/x.cs", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_AfterTheFileWasGone_DropsTheCachedRulesEvenIfTheStampComesBack()
    {
        var file = Path.Combine(_root, IgnoreRulesProvider.FileName);
        await File.WriteAllTextAsync(file, "foo/\n", TestContext.Current.CancellationToken);
        var stamp = new FileInfo(file).LastWriteTimeUtc;
        await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        File.Delete(file);
        (await _provider.LoadAsync(_root, TestContext.Current.CancellationToken)).HasRules.ShouldBeFalse();

        // Same length, same timestamp: only the load that saw the file absent can make this read again.
        await File.WriteAllTextAsync(file, "bar/\n", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(file, stamp);

        var after = await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        after.IsIgnored("bar/x.cs", isDirectory: false).ShouldBeTrue("a rules file that was gone must not be served from before the gap");
    }

    [Fact]
    public async Task LoadAsync_ChangedIgnoreFile_ReturnsTheNewRules()
    {
        var file = Path.Combine(_root, IgnoreRulesProvider.FileName);
        await File.WriteAllTextAsync(file, "bin/\n", TestContext.Current.CancellationToken);
        (await _provider.LoadAsync(_root, TestContext.Current.CancellationToken))
            .IsIgnored("bin/x.cs", isDirectory: false).ShouldBeTrue();

        await File.WriteAllTextAsync(file, "obj/\n*.generated.cs\n", TestContext.Current.CancellationToken);

        var second = await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        second.IsIgnored("obj/x.cs", isDirectory: false).ShouldBeTrue();
        second.IsIgnored("bin/x.cs", isDirectory: false).ShouldBeFalse("the cache must be keyed on the file, not kept forever");
    }
}
