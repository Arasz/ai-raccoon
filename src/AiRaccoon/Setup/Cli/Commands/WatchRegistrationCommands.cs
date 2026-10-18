using System.CommandLine;
using AiRaccoon.Settings;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>`watch add|remove|status`: the server's watch service through <see cref="IWatchControl" />, one fact per stdout line.</summary>
internal sealed class WatchRegistrationCommands
{
    private readonly IWatchControl _control;

    public WatchRegistrationCommands(IWatchControl control)
    {
        Guard.IsNotNull(control);
        _control = control;
    }

    public async Task<int> AddAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var projectId = parseResult.GetValue<string>("project-id")!;
        var path = Path.GetFullPath(parseResult.GetValue<string>("path")!);
        var added = await _control.AddAsync(projectId, path, cancellationToken);

        await streams.WriteOutputLineAsync($"project: {added.ProjectId}");
        await streams.WriteOutputLineAsync($"registered: {added.Path}");
        foreach (var pruned in added.Pruned)
        {
            await streams.WriteOutputLineAsync($"pruned: {pruned}");
        }

        if (added.AbsorbedBy is { } absorbedBy)
        {
            await streams.WriteOutputLineAsync($"absorbedBy: {absorbedBy}");
        }

        return ErrorCode.Ok.Success;
    }

    public async Task<int> RemoveAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var projectId = parseResult.GetValue<string>("project-id")!;
        var path = Path.GetFullPath(parseResult.GetValue<string>("path")!);
        var removed = await _control.RemoveAsync(projectId, path, cancellationToken);
        await streams.WriteOutputLineAsync(removed.Removed is not false
            ? $"removed: {removed.Path}"
            : $"not watched: {removed.Path} (nothing removed)");
        return ErrorCode.Ok.Success;
    }

    public async Task<int> StatusAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var statuses = await _control.StatusAsync(parseResult.GetValue<string?>("project-id"), cancellationToken);
        if (statuses.Count == 0)
        {
            await streams.WriteOutputLineAsync("no registered watches");
            return ErrorCode.Ok.Success;
        }

        for (var i = 0; i < statuses.Count; i++)
        {
            if (i > 0)
            {
                await streams.WriteOutputLineAsync(string.Empty);
            }

            foreach (var line in WatchStatusFormat.Render(statuses[i]))
            {
                await streams.WriteOutputLineAsync(line);
            }
        }

        return ErrorCode.Ok.Success;
    }
}
