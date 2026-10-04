using System.CommandLine;
using AiRaccoon.Core.Projects;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `project id register | get | check` over the server's project directory. A blank argument is
///     refused before any server is contacted; success writes one stdout line and nothing to stderr.
///     No verb reads stdin.
/// </summary>
internal sealed class ProjectIdCommands(IProjectDirectory directory)
{
    public async Task<int> RegisterAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var projectId = parseResult.GetValue<string>("id");
        var name = parseResult.GetValue<string?>("--name");
        if (string.IsNullOrWhiteSpace(projectId) || (name is not null && string.IsNullOrWhiteSpace(name)))
        {
            return await UsageAsync(streams, "project id register needs a non-blank <id>, and --name, when given, must not be blank");
        }

        if (ProjectName.TryGetRefusal(name) is { } nameRefusal)
        {
            return await UsageAsync(streams, $"project id register: --name {nameRefusal}");
        }

        var registration = await directory.RegisterAsync(projectId, name, cancellationToken);
        switch (registration.Outcome)
        {
            case ProjectRegistrationOutcome.Registered:
                await streams.WriteOutputLineAsync($"registered {ProjectIdText.Printable(registration.ProjectId)}");
                return ErrorCode.Ok.Success;
            case ProjectRegistrationOutcome.AlreadyRegistered:
                await streams.WriteOutputLineAsync($"already registered {ProjectIdText.Printable(registration.ProjectId)}");
                return ErrorCode.Ok.Success;
            case ProjectRegistrationOutcome.Retired:
                await streams.WriteErrorLineAsync($"ai-raccoon: {new RetiredProjectException(registration.ProjectId).Message}");
                return ErrorCode.Usage.ProjectUnknown;
            default:
                return await UsageAsync(streams,
                    $"'{ProjectIdText.Printable(registration.ProjectId)}' is not a guid, and no project is registered or holds rows under it; register a guid instead");
        }
    }

    public async Task<int> GetAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var name = parseResult.GetValue<string>("--name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return await UsageAsync(streams, "project id get needs a non-blank --name");
        }

        var ids = await directory.FindByNameAsync(name, cancellationToken);
        switch (ids.Count)
        {
            case 1:
                await streams.WriteOutputLineAsync(ids[0]);
                return ErrorCode.Ok.Success;
            case 0:
                await streams.WriteErrorLineAsync($"ai-raccoon: no project is registered under the name '{ProjectIdText.Printable(name)}'");
                return ErrorCode.Usage.ProjectUnknown;
            default:
                await streams.WriteErrorLineAsync(
                    $"ai-raccoon: {ids.Count} projects are registered under the name '{ProjectIdText.Printable(name)}'; pick one of:");
                foreach (var id in ids)
                {
                    await streams.WriteErrorLineAsync(ProjectIdText.Printable(id));
                }

                return ErrorCode.Usage.ProjectAmbiguous;
        }
    }

    public async Task<int> CheckAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var projectId = parseResult.GetValue<string>("id");
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return await UsageAsync(streams, "project id check needs a non-blank <id>");
        }

        var check = await directory.CheckAsync(projectId, cancellationToken);
        var status = check.Status switch
        {
            ProjectIdStatus.Known => "known",
            ProjectIdStatus.Retired => "retired",
            _ => "unknown"
        };
        await streams.WriteOutputLineAsync($"{status} {ProjectIdText.Printable(check.ProjectId)}");
        return check.Status == ProjectIdStatus.Known ? ErrorCode.Ok.Success : ErrorCode.Usage.ProjectUnknown;
    }

    private static async Task<int> UsageAsync(StandardStreams streams, string message)
    {
        await streams.WriteErrorLineAsync($"ai-raccoon: {message}");
        return ErrorCode.Usage.InvalidValue;
    }
}
