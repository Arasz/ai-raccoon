using System.Text;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

/// <summary>
///     File-reading half of `ai-raccoon.ignore` (Core's IgnoreRules stays pure/no-I/O): reads
///     `&lt;root&gt;/ai-raccoon.ignore` through an injected file provider and resolves to
///     <c>IgnoreRules.Empty</c> when the file is missing. Rules are cached per root behind a change
///     token taken before the read (ADR-0152), so a change that lands during a read is never lost.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class IgnoreRulesProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ignore-rules-provider-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeProviders _fakes = new();
    private readonly List<IgnoreRulesProvider> _owned = [];

    public IgnoreRulesProviderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var provider in _owned)
        {
            provider.Dispose();
        }

        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private IgnoreRulesProvider Fake()
    {
        var provider = new IgnoreRulesProvider(_fakes.Create);
        _owned.Add(provider);
        return provider;
    }

    [Fact]
    public async Task LoadAsync_NoIgnoreFile_ReturnsEmptyRules()
    {
        var provider = Real();

        (await provider.LoadAsync(_root, TestContext.Current.CancellationToken)).HasRules.ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_RealFile_ParsesOnceAndInvalidateReadsTheNewContent()
    {
        var provider = Real();
        var file = Path.Combine(_root, IgnoreRulesProvider.FileName);
        await File.WriteAllTextAsync(file, "foo/\n", TestContext.Current.CancellationToken);

        var first = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        var second = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        second.ShouldBeSameAs(first, "an unmoved ignore file must not be re-read and re-parsed on every filesystem event");
        first.IsIgnored("foo/x.cs", isDirectory: false).ShouldBeTrue();

        await File.WriteAllTextAsync(file, "bar/\n", TestContext.Current.CancellationToken);
        provider.Invalidate(_root);

        var after = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        after.IsIgnored("bar/x.cs", isDirectory: false).ShouldBeTrue();
        after.IsIgnored("foo/x.cs", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_ChangeTokenFiresDuringTheRead_TheNextLoadReadsAgain()
    {
        var provider = Fake();
        var fake = _fakes.For(_root);
        fake.Content = "foo/\n";
        fake.OnRead = fake.FireChange;

        var first = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        first.IsIgnored("foo/x.cs", isDirectory: false).ShouldBeTrue();

        fake.OnRead = null;
        fake.Content = "bar/\n";
        var second = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        fake.Reads.ShouldBe(2, "a change that landed during the first read must not leave that parse servable");
        second.IsIgnored("bar/x.cs", isDirectory: false).ShouldBeTrue();
        second.IsIgnored("foo/x.cs", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public async Task Invalidate_DuringTheRead_TheNextLoadReadsAgain()
    {
        var provider = Fake();
        var fake = _fakes.For(_root);
        fake.Content = "foo/\n";
        fake.OnRead = () => provider.Invalidate(_root);

        await provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        fake.OnRead = null;
        fake.Content = "bar/\n";
        var second = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        fake.Reads.ShouldBe(2);
        second.IsIgnored("bar/x.cs", isDirectory: false).ShouldBeTrue();
    }

    [Fact]
    public async Task LoadAsync_TokenNotFired_ServesTheCachedRulesWithoutReading()
    {
        var provider = Fake();
        var fake = _fakes.For(_root);
        fake.Content = "foo/\n";

        var first = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        var second = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        second.ShouldBeSameAs(first);
        fake.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task LoadAsync_ChangeTokenFiresAfterCaching_TheNextLoadReadsAgain()
    {
        var provider = Fake();
        var fake = _fakes.For(_root);
        fake.Content = "foo/\n";
        await provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        fake.Content = "bar/\n";
        fake.FireChange();
        var second = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        fake.Reads.ShouldBe(2);
        second.IsIgnored("bar/x.cs", isDirectory: false).ShouldBeTrue();
    }

    [Fact]
    public async Task LoadAsync_FileAbsentThenPresent_ReadsTheFileWhenItAppears()
    {
        var provider = Fake();
        var fake = _fakes.For(_root);

        (await provider.LoadAsync(_root, TestContext.Current.CancellationToken)).HasRules.ShouldBeFalse();
        fake.Content = "foo/\n";

        var after = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        after.IsIgnored("foo/x.cs", isDirectory: false).ShouldBeTrue();
    }

    [Fact]
    public async Task LoadAsync_FileDeletedThenRestoredWithTheSameContent_ReadsAgain()
    {
        var provider = Fake();
        var fake = _fakes.For(_root);
        fake.Content = "foo/\n";
        await provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        fake.Content = null;
        fake.FireChange();
        (await provider.LoadAsync(_root, TestContext.Current.CancellationToken)).HasRules.ShouldBeFalse();
        fake.Content = "foo/\n";
        var restored = await provider.LoadAsync(_root, TestContext.Current.CancellationToken);

        restored.IsIgnored("foo/x.cs", isDirectory: false).ShouldBeTrue();
        fake.Reads.ShouldBe(2, "an absent file must not leave an entry that serves rules from before the gap");
    }

    [Fact]
    public async Task LoadAsync_FileVanishesBetweenTheStatAndTheRead_ResolvesToEmpty()
    {
        var provider = Fake();
        var fake = _fakes.For(_root);
        fake.Content = "foo/\n";
        fake.ThrowOnRead = true;

        (await provider.LoadAsync(_root, TestContext.Current.CancellationToken)).HasRules.ShouldBeFalse();
    }

    [Fact]
    public async Task Dispose_DisposesEveryRootsFileProvider()
    {
        var provider = new IgnoreRulesProvider(_fakes.Create);
        var other = Path.Combine(_root, "other");
        await provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        await provider.LoadAsync(other, TestContext.Current.CancellationToken);

        provider.Dispose();

        _fakes.All.Count.ShouldBe(2);
        _fakes.All.ShouldAllBe(fake => fake.Disposed);
    }

    [Fact]
    public async Task LoadAsync_ForTheSameRootSpelledTwoWays_UsesOneFileProvider()
    {
        var provider = Fake();

        await provider.LoadAsync(_root, TestContext.Current.CancellationToken);
        await provider.LoadAsync(_root + Path.DirectorySeparatorChar, TestContext.Current.CancellationToken);

        _fakes.All.Count.ShouldBe(1);
    }

    /// <summary>The name check is the same on every host once the comparison is chosen (ADR-0152).</summary>
    [Fact]
    public void IsIgnoreFileName_FollowsTheComparison()
    {
        IgnoreRulesProvider.IsIgnoreFileName("AI-Raccoon.IGNORE", StringComparison.Ordinal).ShouldBeFalse();
        IgnoreRulesProvider.IsIgnoreFileName("AI-Raccoon.IGNORE", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        IgnoreRulesProvider.IsIgnoreFileName("ai-raccoon.ignore", StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void NameComparison_IsCaseInsensitiveOnWindowsAndMacOnly()
    {
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        IgnoreRulesProvider.NameComparison.ShouldBe(expected);
        IgnoreRulesProvider.IsIgnoreFileName("AI-RACCOON.IGNORE").ShouldBe(expected == StringComparison.OrdinalIgnoreCase);
    }

    private IgnoreRulesProvider Real()
    {
        var provider = new IgnoreRulesProvider(IgnoreRulesProvider.CreatePhysicalProvider);
        _owned.Add(provider);
        return provider;
    }

    private sealed class FakeProviders
    {
        private readonly Dictionary<string, FakeFileProvider> _byRoot = new(IngestPath.PathComparer);

        public List<FakeFileProvider> All { get; } = [];

        public IFileProvider Create(string root) => For(root);

        public FakeFileProvider For(string root)
        {
            var key = IngestPath.Normalize(root);
            if (!_byRoot.TryGetValue(key, out var fake))
            {
                fake = new FakeFileProvider();
                _byRoot[key] = fake;
                All.Add(fake);
            }

            return fake;
        }
    }

    private sealed class FakeFileProvider : IFileProvider, IDisposable
    {
        private CancellationTokenSource _changed = new();

        /// <summary>The file text, or null when the file does not exist.</summary>
        public string? Content { get; set; }

        public Action? OnRead { get; set; }

        public bool ThrowOnRead { get; set; }

        public int Reads { get; private set; }

        public bool Disposed { get; private set; }

        public void FireChange()
        {
            var fired = _changed;
            _changed = new CancellationTokenSource();
            fired.Cancel();
        }

        public void Dispose() => Disposed = true;

        public IChangeToken Watch(string filter) => new CancellationChangeToken(_changed.Token);

        public IFileInfo GetFileInfo(string subpath) => new FakeFileInfo(this);

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        private sealed class FakeFileInfo(FakeFileProvider owner) : IFileInfo
        {
            public bool Exists => owner.Content is not null;

            public long Length => -1;

            public string? PhysicalPath => null;

            public string Name => IgnoreRulesProvider.FileName;

            public DateTimeOffset LastModified => DateTimeOffset.MinValue;

            public bool IsDirectory => false;

            public Stream CreateReadStream()
            {
                owner.Reads++;
                var text = owner.Content;
                owner.OnRead?.Invoke();
                if (owner.ThrowOnRead)
                {
                    throw new FileNotFoundException();
                }

                return new MemoryStream(Encoding.UTF8.GetBytes(text ?? string.Empty));
            }
        }
    }
}
