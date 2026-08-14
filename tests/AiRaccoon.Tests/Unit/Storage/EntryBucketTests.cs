using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     EntryBucket.For picks the (scope, project_id, context_label, workspace_id) columns every
///     write lands in, so a mis-mapped context leaks rows across projects or out of a workspace.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryBucketTests
{
    private const string Caller = "acme";

    [Theory]
    // shared: the caller's project id is kept, but scope is what selects the row.
    [InlineData("shared", "shared", Caller, null, null)]
    // project: only the caller's own project is admitted; naming another is refused, not mapped.
    [InlineData("project:acme", "project", Caller, null, null)]
    // workspace: no scope at all — workspace rows are selected by workspace_id.
    [InlineData("workspace:ws-1", null, Caller, null, "ws-1")]
    // anything else is a custom label under the caller's project.
    [InlineData("docs-notes", "custom", Caller, "docs-notes", null)]
    [InlineData("", "custom", Caller, "", null)]
    public void For_MapsEachContextShapeToItsColumns(
        string context, string? scope, string projectId, string? contextLabel, string? workspaceId)
    {
        var bucket = EntryBucket.For(context, Caller);

        bucket.Scope.ShouldBe(scope);
        bucket.ProjectId.ShouldBe(projectId);
        bucket.ContextLabel.ShouldBe(contextLabel);
        bucket.WorkspaceId.ShouldBe(workspaceId);
    }

    /// <summary>A project context re-targets the row: the caller's own project id is discarded.</summary>
    [Fact]
    public void For_WithTheCallersOwnProjectContext_StaysInThatProject()
    {
        var bucket = EntryBucket.For(ContextNaming.ProjectContext(Caller), Caller);

        bucket.ProjectId.ShouldBe(Caller);
        bucket.Scope.ShouldBe("project");
        bucket.ContextLabel.ShouldBeNull();
        bucket.WorkspaceId.ShouldBeNull();
    }

    [Fact]
    public void For_WithAnotherProjectsContext_IsRefused()
    {
        // The access gate authorises the request's projectId; without this a caller cleared for
        // one project could land rows in another just by naming it in the context.
        Should.Throw<ContextOutsideProjectException>(
                () => EntryBucket.For(ContextNaming.ProjectContext("beta"), Caller))
            .Message.ShouldContain("beta");
    }

    /// <summary>A workspace row stays inside the caller's project and carries no scope.</summary>
    [Fact]
    public void For_WithAWorkspaceContext_KeepsTheCallersProjectAndLeavesScopeUnset()
    {
        var bucket = EntryBucket.For(ContextNaming.WorkspaceContext("ws-1"), "beta");

        bucket.WorkspaceId.ShouldBe("ws-1");
        bucket.ProjectId.ShouldBe("beta");
        bucket.Scope.ShouldBeNull();
        bucket.ContextLabel.ShouldBeNull();
    }

    /// <summary>Reads the shapes ContextNaming actually emits, so a prefix change on one side fails here.</summary>
    [Fact]
    public void For_ReadsTheContextStringsContextNamingProduces()
    {
        EntryBucket.For(ContextNaming.SharedContext, Caller).Scope.ShouldBe("shared");
        EntryBucket.For(ContextNaming.ProjectContext(Caller), Caller).ProjectId.ShouldBe(Caller);
        EntryBucket.For(ContextNaming.WorkspaceContext("ws-1"), Caller).WorkspaceId.ShouldBe("ws-1");
    }

    /// <summary>Prefix matching is ordinal: a differently-cased prefix is a label, never a re-target.</summary>
    [Theory]
    [InlineData("Shared")]
    [InlineData("PROJECT:beta")]
    [InlineData("Workspace:ws-1")]
    [InlineData(" shared")]
    [InlineData("shared ")]
    public void For_WithADifferentlyCasedOrPaddedPrefix_FallsBackToACustomLabel(string context)
    {
        var bucket = EntryBucket.For(context, Caller);

        bucket.Scope.ShouldBe("custom");
        bucket.ProjectId.ShouldBe(Caller);
        bucket.ContextLabel.ShouldBe(context);
        bucket.WorkspaceId.ShouldBeNull();
    }

    /// <summary>The prefixes are tested in order, so the outer one wins when both are present.</summary>
    [Fact]
    public void For_WithBothPrefixesNested_TakesTheOuterOne()
    {
        // The outer prefix still wins: it is read as a project named "workspace:ws-1", which is
        // not the caller's, so it is refused rather than silently re-targeted.
        Should.Throw<ContextOutsideProjectException>(() => EntryBucket.For("project:workspace:ws-1", Caller));

        var asWorkspace = EntryBucket.For("workspace:project:beta", Caller);
        asWorkspace.Scope.ShouldBeNull();
        asWorkspace.WorkspaceId.ShouldBe("project:beta");
        asWorkspace.ProjectId.ShouldBe(Caller);
    }

    /// <summary>"shared" is matched whole, not as a prefix: a longer string is a label.</summary>
    [Fact]
    public void For_WithAContextMerelyStartingWithShared_IsACustomLabel()
    {
        var bucket = EntryBucket.For("shared-notes", Caller);

        bucket.Scope.ShouldBe("custom");
        bucket.ContextLabel.ShouldBe("shared-notes");
    }

    /// <summary>An empty workspace suffix is admitted; an empty project suffix names no project and is refused.</summary>
    [Theory]
    [InlineData("workspace:", null, Caller, "")]
    public void For_WithAnEmptySuffix_StillTakesThePrefixBranch(
        string context, string? scope, string projectId, string? workspaceId)
    {
        var bucket = EntryBucket.For(context, Caller);

        bucket.Scope.ShouldBe(scope);
        bucket.ProjectId.ShouldBe(projectId);
        bucket.WorkspaceId.ShouldBe(workspaceId);
    }

    [Fact]
    public void For_WithAnEmptyProjectSuffix_IsRefused()
    {
        Should.Throw<ContextOutsideProjectException>(() => EntryBucket.For("project:", Caller));
    }

    /// <summary>
    ///     "label:{project}:{label}" is stored whole, while MemorySql.ContextKeyFor strips the
    ///     prefix — the two disagree on the same context string (see the report on this lane).
    /// </summary>
    [Fact]
    public void For_WithALabelContext_StripsThePrefixTheSameWayContextKeyForDoes()
    {
        // Divergence here is invisible: the vec0 partition key and the text filter would disagree,
        // so a labelled row is reachable by vector search and not by keyword search.
        var context = ContextNaming.LabelContext(Caller, "docs");

        var bucket = EntryBucket.For(context, Caller);

        bucket.Scope.ShouldBe("custom");
        bucket.ContextLabel.ShouldBe("docs");
        MemorySql.ContextKeyFor(context, Caller).ShouldBe("custom:4:acme:docs");
    }

    [Fact]
    public void For_WithALabelContextNamingAnotherProject_IsRefused()
    {
        Should.Throw<ContextOutsideProjectException>(
            () => EntryBucket.For(ContextNaming.LabelContext("beta", "docs"), Caller));
    }
}
