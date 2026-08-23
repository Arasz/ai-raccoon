using System.ComponentModel;
using AiRaccoon.Core.Projects;
using JetBrains.Annotations;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tool over IProjectRegistry — mints and registers a project id (ADR-0089 decision 4), no business logic here.</summary>
public sealed class ProjectTools(
    IProjectRegistry registry,
    IToolGate gate)
{
    private const string TnProjectIdTokenGet = "project_id_token_get";

    private const string Instructions =
        "This id is where the project's memory lives from now on — not a secret and not access "
        + "control, just which project's memory a call reaches. Store it (e.g. in a local file kept "
        + "out of memory) and pass it as projectId on every later call for this project; losing it "
        + "means the project's memory becomes unreachable, not deleted.";

    [McpServerTool(Name = TnProjectIdTokenGet)]
    [Description(
        "Mints a new project id: a guidv7, registered so it is a real project from this call on. Call this once per project and keep the returned id — every other tool's projectId parameter takes it.")]
    public async Task<ApiEnvelope<ProjectIdTokenResult>> Get(
        [Description("Optional human-facing label for the new project. Not unique, and never accepted where a project id is expected.")]
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        await gate.RequireBankAvailableAsync(TnProjectIdTokenGet, cancellationToken).ConfigureAwait(false);

        var projectId = Guid.CreateVersion7().ToString("D");
        await registry.RegisterAsync(projectId, name, cancellationToken).ConfigureAwait(false);

        var result = new ProjectIdTokenResult(projectId, Instructions);
        return await gate.WrapAsync(projectId, result, cancellationToken).ConfigureAwait(false);
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record ProjectIdTokenResult(string ProjectId, string Instructions);
}
