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
    // project: the id comes out of the context, not the caller.
    [InlineData("project:acme", "project", "acme", null, null)]
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
    public void For_WithAProjectContext_TakesTheProjectIdFromTheContext()
    {
        var bucket = EntryBucket.For(ContextNaming.ProjectContext("beta"), Caller);

        bucket.ProjectId.ShouldBe("beta");
        bucket.Scope.ShouldBe("project");
        bucket.ContextLabel.ShouldBeNull();
        bucket.WorkspaceId.ShouldBeNull();
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
        EntryBucket.For(ContextNaming.ProjectContext("beta"), Caller).ProjectId.ShouldBe("beta");
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
        var asProject = EntryBucket.For("project:workspace:ws-1", Caller);
        asProject.Scope.ShouldBe("project");
        asProject.ProjectId.ShouldBe("workspace:ws-1");
        asProject.WorkspaceId.ShouldBeNull();

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

    /// <summary>An empty suffix is admitted: "project:" re-targets the row onto an empty project id.</summary>
    [Theory]
    [InlineData("project:", "project", "", null)]
    [InlineData("workspace:", null, Caller, "")]
    public void For_WithAnEmptySuffix_StillTakesThePrefixBranch(
        string context, string? scope, string projectId, string? workspaceId)
    {
        var bucket = EntryBucket.For(context, Caller);

        bucket.Scope.ShouldBe(scope);
        bucket.ProjectId.ShouldBe(projectId);
        bucket.WorkspaceId.ShouldBe(workspaceId);
    }

    /// <summary>
    ///     "label:{project}:{label}" is stored whole, while MemorySql.ContextKeyFor strips the
    ///     prefix — the two disagree on the same context string (see the report on this lane).
    /// </summary>
    [Fact]
    public void For_WithALabelContext_KeepsThePrefixThatContextKeyForStrips()
    {
        var context = ContextNaming.LabelContext(Caller, "docs");

        var bucket = EntryBucket.For(context, Caller);

        bucket.Scope.ShouldBe("custom");
        bucket.ContextLabel.ShouldBe("label:acme:docs");
        MemorySql.ContextKeyFor(context, Caller).ShouldBe("custom:4:acme:docs");
    }
}
