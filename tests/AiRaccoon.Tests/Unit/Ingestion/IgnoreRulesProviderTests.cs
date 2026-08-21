using AiRaccoon.Infrastructure.Ingestion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

/// <summary>
///     File-reading half of `ai-raccoon.ignore` (Core's IgnoreRules stays pure/no-I/O): reads
///     `&lt;root&gt;/ai-raccoon.ignore` fresh on every call — no cache (plan §2.1/§5.2) — and
///     resolves to <c>IgnoreRules.Empty</c> when the file is missing.
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

    [Fact]
    public async Task LoadAsync_ReadsFresh_EveryCall_NoCache()
    {
        var first = await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        first.HasRules.ShouldBeFalse();

        await File.WriteAllTextAsync(Path.Combine(_root, IgnoreRulesProvider.FileName), "bin/\n",
            TestContext.Current.CancellationToken);

        var second = await _provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        second.HasRules.ShouldBeTrue();
    }
}
