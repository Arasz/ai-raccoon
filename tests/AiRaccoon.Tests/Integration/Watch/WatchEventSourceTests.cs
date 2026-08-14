using System.Diagnostics;
using AiRaccoon.Infrastructure.Watch;
using Microsoft.Extensions.Logging.Abstractions;
using AiRaccoon.Tests.Unit.Watch;
using Shouldly;
using Xunit;
using Xunit.Sdk;

namespace AiRaccoon.Tests.Integration.Watch;

/// <summary>
///     FileSystemWatcher adapter: the four event types translate to WatchEvent with normalized
///     paths, and adapter failures never throw — they surface as synthetic WatchEventError events.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
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

    // FileSystemWatcher only accepts directories, so a FILE registration watches the parent
    // directory and translates events for that file only.

    [Fact]
    public void Start_OnFilePath_IsWatching_AndNoErrorEvent()
    {
        using var dir = TempDir.New("source-file-start");
        var file = dir.File("watch-me.md");
        File.WriteAllText(file, "x");
        var errors = new List<WatchEventError>();
        var source = NewSource([], errors);

        Should.NotThrow(() => source.Start(Project, file));

        source.IsWatching(Project, file).ShouldBeTrue();
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Start_OnMissingFile_WithExistingParent_IsWatching_AndNoErrorEvent()
    {
        using var dir = TempDir.New("source-file-missing");
        var file = dir.File("not-yet.md");
        var errors = new List<WatchEventError>();
        var source = NewSource([], errors);

        Should.NotThrow(() => source.Start(Project, file));

        source.IsWatching(Project, file).ShouldBeTrue();
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void FileWatch_CreatedAndChanged_FireForTargetFile_NotForSiblings()
    {
        using var dir = TempDir.New("source-file-created");
        var file = dir.File("watch-me.md");
        File.WriteAllText(file, "v1");
        var events = new List<WatchEvent>();
        var source = NewSource(events, []);

        source.Start(Project, file);
        WaitFor(() => source.IsWatching(Project, file), "watcher start");

        File.WriteAllText(file, "v2");
        WaitFor(() => events.Any(e => e.Kind == WatchEventKind.Changed && e.Path == file), "changed event for target");

        File.WriteAllText(dir.File("sibling.md"), "noise");

        // Sentinel: the target's first Deleted is queued after the sibling write, so its arrival
        // proves the watcher drained past that write. Absence only means anything after it.
        File.Delete(file);
        WaitFor(() => events.Any(e => e.Kind == WatchEventKind.Deleted && e.Path == file), "sentinel delete for target");
        events.ShouldNotContain(e => e.Path == dir.File("sibling.md"));
    }

    [Fact]
    public void FileWatch_Deleted_FiresForTargetFile()
    {
        using var dir = TempDir.New("source-file-deleted");
        var file = dir.File("watch-me.md");
        File.WriteAllText(file, "x");
        var events = new List<WatchEvent>();
        var source = NewSource(events, []);

        source.Start(Project, file);
        WaitFor(() => source.IsWatching(Project, file), "watcher start");

        File.Delete(file);
        WaitFor(() => events.Any(e => e.Kind == WatchEventKind.Deleted && e.Path == file), "deleted event for target");
    }

    [Fact]
    public void FileWatch_Recreate_FiresCreatedForTargetFile()
    {
        using var dir = TempDir.New("source-file-recreate");
        var file = dir.File("watch-me.md");
        var events = new List<WatchEvent>();
        var source = NewSource(events, []);

        source.Start(Project, file);
        WaitFor(() => source.IsWatching(Project, file), "watcher start");

        File.WriteAllText(file, "v2");
        WaitFor(() => events.Any(e => e.Kind == WatchEventKind.Changed && e.Path == file), "changed event for target");

        File.Delete(file);
        WaitFor(() => events.Any(e => e.Kind == WatchEventKind.Deleted && e.Path == file), "deleted event for target");
        File.WriteAllText(file, "v3");
        WaitFor(() => events.Any(e => e.Kind == WatchEventKind.Created && e.Path == file), "created event for target");
    }

    [Fact]
    public void FileWatch_RenameAway_EmitsDeletedForRegisteredPath()
    {
        using var dir = TempDir.New("source-file-rename-away");
        var file = dir.File("watch-me.md");
        File.WriteAllText(file, "x");
        var events = new List<WatchEvent>();
        var source = NewSource(events, []);

        source.Start(Project, file);
        WaitFor(() => source.IsWatching(Project, file), "watcher start");

        File.Move(file, dir.File("watch-me.md.bak"));
        WaitFor(() => events.Any(e => e.Kind == WatchEventKind.Deleted && e.Path == file), "deleted event for target");
        events.ShouldNotContain(e => e.Kind == WatchEventKind.Renamed);
    }

    [Fact]
    public void FileWatch_RenameInto_EmitsRenamedWithOldPath()
    {
        using var dir = TempDir.New("source-file-rename-in");
        var file = dir.File("watch-me.md");
        var tmp = dir.File("tmp-upload");
        File.WriteAllText(tmp, "x");
        var events = new List<WatchEvent>();
        var source = NewSource(events, []);

        source.Start(Project, file);
        WaitFor(() => source.IsWatching(Project, file), "watcher start");

        File.Move(tmp, file);
        WaitFor(() => events.Any(e => e.Kind == WatchEventKind.Renamed && e.Path == file && e.OldPath == tmp),
            "renamed event for target");
    }

    private static void WaitFor(Func<bool> condition, string what, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(25);
        }

        throw new XunitException($"Timed out after {timeoutMs} ms waiting for {what}.");
    }
}
