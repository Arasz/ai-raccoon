using System.ComponentModel;
using AiRaccoon.Core.Projects;
using CommunityToolkit.Diagnostics;
using JetBrains.Annotations;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tools over the project registry and directory: look an id up by name, or mint and register one (ADR-0089 decision 4).</summary>
public sealed class ProjectTools(
    IProjectRegistry registry,
    IProjectDirectory directory,
    IToolGate gate)
{
    private const string TnProjectIdTokenGet = "project_id_token_get";
    private const string TnProjectIdGet = "project_id_get";

    private const string Instructions =
        "This id is where the project's memory lives from now on — not a secret and not access "
        + "control, just which project's memory a call reaches. Store it (e.g. in a local file kept "
        + "out of memory) and pass it as projectId on every later call for this project; losing it "
        + "means the project's memory becomes unreachable, not deleted.";

    [McpServerTool(Name = TnProjectIdTokenGet)]
    [Description(
        "Call project_id_get first; mint only when it finds nothing. Mints a new project id: a guidv7, registered so it is a real project from this call on. Call this once per project and keep the returned id — every other tool's projectId parameter takes it.")]
    public async Task<ApiEnvelope<ProjectIdTokenResult>> Get(
        [Description("Optional human-facing label for the new project. Not unique, and never accepted where a project id is expected.")]
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireBankAvailableAsync(TnProjectIdTokenGet, cancellationToken);

        var projectId = Guid.CreateVersion7().ToString("D");
        await registry.RegisterAsync(projectId, name, cancellationToken);

        var result = new ProjectIdTokenResult(projectId, Instructions);
        return await gate.WrapAsync(projectId, result, cancellationToken);
    }

    [McpServerTool(Name = TnProjectIdGet)]
    [Description(
        "Returns the project id registered under an exact, case-sensitive name, as stored. Call this before project_id_token_get. No match refuses with project-not-found (then mint one with project_id_token_get); several matches refuse with project-name-ambiguous, listing every id, so ask the user which one. Never registers anything.")]
    public async Task<ApiEnvelope<ProjectIdGetResult>> FindByName(
        [Description("The project's name, matched exactly and case-sensitively.")]
        string name,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(name);
        await gate.RequireBankAvailableAsync(TnProjectIdGet, cancellationToken);

        var ids = await directory.FindByNameAsync(name, cancellationToken);
        var projectId = ProjectNameMatch.Single(name, ids);
        return await gate.WrapAsync(projectId, new ProjectIdGetResult(projectId), cancellationToken);
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ProjectIdTokenResult(string ProjectId, string Instructions);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ProjectIdGetResult(string ProjectId);
}
