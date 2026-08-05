using AiRaccoon.Infrastructure.Watch;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>
///     FileSystemWatcher adapter: the four event types translate to WatchEvent with D3-normalized
///     paths, and adapter failures never throw — they surface as synthetic WatchEventError events.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchEventSourceTests
{
    private const string Project = "acme";

    private static WatchEventSource NewSource(List<WatchEvent> events, List<WatchEventError> errors) => new(events.Add, errors.Add, NullLogger<WatchEventSource>.Instance);

    [Fact]
    public void Created_TranslatesToWatchEvent_WithNormalizedPath()
    {
        using var dir = TempDir.New("source-created");
        var events = new List<WatchEvent>();
        var source = NewSource(events, []);

        source.HandleCreated(Project, dir.Path,
            new FileSystemEventArgs(WatcherChangeTypes.Created, dir.Path + Path.DirectorySeparatorChar, "a.md"));

        var evt = events.ShouldHaveSingleItem();
        evt.ProjectId.ShouldBe(Project);
        evt.Path.ShouldBe(dir.File("a.md"));
        evt.Kind.ShouldBe(WatchEventKind.Created);
        evt.OldPath.ShouldBeNull();
    }

    [Fact]
    public void Changed_TranslatesToChangedEvent()
    {
        using var dir = TempDir.New("source-changed");
        var events = new List<WatchEvent>();
        var source = NewSource(events, []);

        source.HandleChanged(Project, dir.Path,
            new FileSystemEventArgs(WatcherChangeTypes.Changed, dir.Path, "a.md"));

        var evt = events.ShouldHaveSingleItem();
        evt.Path.ShouldBe(dir.File("a.md"));
        evt.Kind.ShouldBe(WatchEventKind.Changed);
    }

    [Fact]
    public void Deleted_TranslatesToDeletedEvent()
    {
        using var dir = TempDir.New("source-deleted");
        var events = new List<WatchEvent>();
        var source = NewSource(events, []);

        source.HandleDeleted(Project, dir.Path,
            new FileSystemEventArgs(WatcherChangeTypes.Deleted, dir.Path, "a.md"));

        var evt = events.ShouldHaveSingleItem();
        evt.Path.ShouldBe(dir.File("a.md"));
        evt.Kind.ShouldBe(WatchEventKind.Deleted);
    }

    [Fact]
    public void Renamed_TranslatesToRenamedEvent_WithNormalizedOldPath()
    {
        using var dir = TempDir.New("source-renamed");
        var events = new List<WatchEvent>();
        var source = NewSource(events, []);

        source.HandleRenamed(Project, dir.Path,
            new RenamedEventArgs(WatcherChangeTypes.Renamed, dir.Path + Path.DirectorySeparatorChar, "b.md", "a.md"));

        var evt = events.ShouldHaveSingleItem();
        evt.Path.ShouldBe(dir.File("b.md"));
        evt.OldPath.ShouldBe(dir.File("a.md"));
        evt.Kind.ShouldBe(WatchEventKind.Renamed);
    }

    [Fact]
    public void HandlerFailure_IsContained_AndEmitsSyntheticErrorEvent()
    {
        using var dir = TempDir.New("source-throw");
        var errors = new List<WatchEventError>();
        var source = new WatchEventSource(_ => throw new InvalidOperationException("boom"), errors.Add,
            NullLogger<WatchEventSource>.Instance);

        Should.NotThrow(() => source.HandleCreated(Project, dir.Path,
            new FileSystemEventArgs(WatcherChangeTypes.Created, dir.Path, "a.md")));

        var error = errors.ShouldHaveSingleItem();
        error.ProjectId.ShouldBe(Project);
        error.WatchPath.ShouldBe(dir.Path);
        error.Message.ShouldContain("boom");
    }

    [Fact]
    public void WatcherError_IsForwardedAsSyntheticErrorEvent()
    {
        using var dir = TempDir.New("source-error");
        var errors = new List<WatchEventError>();
        var source = NewSource([], errors);

        source.HandleError(Project, dir.Path, new ErrorEventArgs(new IOException("buffer overflow")));

        var error = errors.ShouldHaveSingleItem();
        error.WatchPath.ShouldBe(dir.Path);
        error.Message.ShouldContain("overflow");
    }

    [Fact]
    public void Start_OnInvalidPath_DoesNotThrow_AndEmitsSyntheticErrorEvent()
    {
        var errors = new List<WatchEventError>();
        var source = NewSource([], errors);

        Should.NotThrow(() => source.Start(Project, ""));

        errors.ShouldHaveSingleItem();
        source.IsWatching(Project, "").ShouldBeFalse();
    }

    [Fact]
    public void Start_StartsWatching_AndStop_DisposesTheWatcher()
    {
        using var dir = TempDir.New("source-start");
        var source = NewSource([], []);

        source.Start(Project, dir.Path);
        source.IsWatching(Project, dir.Path).ShouldBeTrue();

        source.Stop(Project, dir.Path);
        source.IsWatching(Project, dir.Path).ShouldBeFalse();
    }

    [Fact]
    public void Start_Twice_IsIdempotent_AndEmitsNoErrorEvents()
    {
        using var dir = TempDir.New("source-twice");
        var errors = new List<WatchEventError>();
        var source = NewSource([], errors);

        source.Start(Project, dir.Path);
        source.Start(Project, dir.Path);

        source.IsWatching(Project, dir.Path).ShouldBeTrue();
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void StopAll_DisposesEveryWatcher()
    {
        using var dir = TempDir.New("source-stopall");
        using var other = TempDir.New("source-stopall-2");
        var source = NewSource([], []);

        source.Start(Project, dir.Path);
        source.Start(Project, other.Path);
        source.StopAll();

        source.IsWatching(Project, dir.Path).ShouldBeFalse();
        source.IsWatching(Project, other.Path).ShouldBeFalse();
    }
}
