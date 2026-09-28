using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     The vec0 `ctx` column (ADR-0068 demoted it from partition key to metadata): one context-key
///     encoding shared by the SQL fragment and its C# twin. Length-prefixed rather than
///     ':'-joined so a project id containing ':' cannot collide with a label containing ':'.
///     Total over a NULL project id (ADR-0124): the key a row with no project id gets is the key
///     <see cref="MemorySql.ContextKeyFor" /> builds from an empty project id, never NULL.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemorySqlContextKeyTests
{
    /// <summary>
    ///     (project_id='a:b', label='c') and (project_id='a', label='b:c') both encode to
    ///     'custom:a:b:c' under naive ':' joining — verified red before the length-prefixed fix.
    /// </summary>
    [Fact]
    public void ContextKeyFor_DoesNotCollide_WhenAProjectIdAndLabelSplitDifferently()
    {
        var first = MemorySql.ContextKeyFor("c", "a:b");
        var second = MemorySql.ContextKeyFor("b:c", "a");

        first.ShouldNotBe(second);
    }

    [Theory]
    [InlineData("shared", "acme", "shared")]
    [InlineData("project:acme", "acme", "project:acme")]
    [InlineData("workspace:ws-1", "acme", "workspace:4:acme:ws-1")]
    [InlineData("docs-notes", "acme", "custom:4:acme:docs-notes")]
    public void ContextKeyFor_EncodesEachContextShape(string context, string projectId, string expected) => MemorySql.ContextKeyFor(context, projectId).ShouldBe(expected);

    /// <summary>
    ///     The pre-fix expression concatenated project_id directly, so every branch but `shared`
    ///     returned SQL NULL for a row with no project id — and vec0's metadata column rejects NULL
    ///     ('Expected text for TEXT metadata column ctx'), breaking MarkEmbedded on such a row.
    /// </summary>
    [Theory]
    [InlineData("project", "project", null, null)]
    [InlineData("shared", "shared", null, null)]
    [InlineData("custom", "custom", "L", null)]
    [InlineData("workspace", null, null, "W")]
    public async Task ContextKeyExpression_WhenProjectIdIsNull_ReturnsAKeyNotNull(
        string shape, string? scope, string? contextLabel, string? workspaceId)
    {
        await using var connection = await OpenScratchAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO scratch(scope, project_id, context_label, workspace_id) VALUES (@scope, NULL, @contextLabel, @workspaceId)",
            new { scope, contextLabel, workspaceId },
            cancellationToken: TestContext.Current.CancellationToken));

        var ctx = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT {MemorySql.ContextKeyExpression("")} FROM scratch",
            cancellationToken: TestContext.Current.CancellationToken));

        ctx.ShouldNotBeNull($"the {shape} shape must key non-NULL for a missing project id");
    }

    /// <summary>
    ///     The two encodings live in two languages; this is the executed check that a missing
    ///     project id cannot make them disagree — the SQL key must equal the C# key built with an
    ///     empty project id, for every context shape.
    /// </summary>
    [Theory]
    [InlineData("shared", "shared", null, null)]
    [InlineData("project:", "project", null, null)]
    [InlineData("workspace:W", null, null, "W")]
    [InlineData("L", "custom", "L", null)]
    [InlineData("", "custom", null, null)]
    public async Task ContextKeyExpression_WhenProjectIdIsNull_EqualsContextKeyForWithEmptyProject(
        string context, string? scope, string? contextLabel, string? workspaceId)
    {
        await using var connection = await OpenScratchAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO scratch(scope, project_id, context_label, workspace_id) VALUES (@scope, NULL, @contextLabel, @workspaceId)",
            new { scope, contextLabel, workspaceId },
            cancellationToken: TestContext.Current.CancellationToken));

        var executed = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT {MemorySql.ContextKeyExpression("")} FROM scratch",
            cancellationToken: TestContext.Current.CancellationToken));

        executed.ShouldBe(MemorySql.ContextKeyFor(context, ""),
            $"the executed SQL key and the C# twin must agree for the '{context}' shape");
    }

    private static async Task<SqliteConnection> OpenScratchAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE TABLE scratch (scope TEXT, project_id TEXT, context_label TEXT, workspace_id TEXT)",
            cancellationToken: TestContext.Current.CancellationToken));
        return connection;
    }
}
