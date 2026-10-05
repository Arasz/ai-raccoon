using AiRaccoon.Core.Projects;

namespace AiRaccoon.Settings;

/// <summary>
///     The wire contract of the control-plane project directory, shared by the endpoint that serves
///     it and the store that calls it so the two halves cannot drift.
/// </summary>
internal static class ProjectsProtocol
{
    public const string Path = "/projects";

    public const string CheckPath = "/projects/check";

    public static string ForName(string name) => $"{Path}?name={Uri.EscapeDataString(name)}";

    public static string ForCheck(string projectId) => $"{CheckPath}?id={Uri.EscapeDataString(projectId)}";
}

/// <summary>A `project id register` request; <see cref="Name" /> is optional but never blank.</summary>
internal sealed record ProjectRegisterRequest(string ProjectId, string? Name);

internal sealed record ProjectRegisterResponse(ProjectRegistrationOutcome Outcome, string ProjectId);

internal sealed record ProjectCheckResponse(ProjectIdStatus Status, string ProjectId);

/// <summary>Every id registered under the requested name; an empty list is a miss, not an error.</summary>
internal sealed record ProjectIdsResponse(IReadOnlyList<string> Ids);
