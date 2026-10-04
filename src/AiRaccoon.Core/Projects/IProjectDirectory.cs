using System.Text.Json.Serialization;

namespace AiRaccoon.Core.Projects;

/// <summary>Looks project ids up by name, checks an id, and registers one — the lookup-before-mint surface.</summary>
public interface IProjectDirectory
{
    /// <summary>Registers an id after folding it; a retired id, an alias or unknown raw text writes nothing.</summary>
    Task<ProjectRegistration> RegisterAsync(string projectId, string? name, CancellationToken cancellationToken = default);

    /// <summary>Every registered id whose name equals <paramref name="name" /> exactly (case-sensitive), ordered by id.</summary>
    Task<IReadOnlyList<string>> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Whether a folded id is a usable project, unknown, or retired. Never registers.</summary>
    Task<ProjectIdCheck> CheckAsync(string projectId, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a register call and the id it applies to (the alias winner, or the canonical spelling).</summary>
public sealed record ProjectRegistration(string ProjectId, ProjectRegistrationOutcome Outcome);

[JsonConverter(typeof(JsonStringEnumConverter<ProjectRegistrationOutcome>))]
public enum ProjectRegistrationOutcome
{
    [JsonStringEnumMemberName("registered")]
    Registered,

    [JsonStringEnumMemberName("already-registered")]
    AlreadyRegistered,

    [JsonStringEnumMemberName("retired")]
    Retired,

    [JsonStringEnumMemberName("not-a-guid")]
    NotAGuid
}

/// <summary>The status of a checked id and the id it resolved to.</summary>
public sealed record ProjectIdCheck(string ProjectId, ProjectIdStatus Status);

[JsonConverter(typeof(JsonStringEnumConverter<ProjectIdStatus>))]
public enum ProjectIdStatus
{
    [JsonStringEnumMemberName("known")]
    Known,

    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("retired")]
    Retired
}
