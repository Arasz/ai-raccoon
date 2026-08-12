namespace AiRaccoon.Core.Watch;

/// <summary>
///     Renders one target's watch config as a labeled block: header with enabled,
///     concurrency and scope, one indented path per line, "(none)" for an empty scope.
/// </summary>
public static class WatchListFormat
{
    public static string Render(string target, WatchConfig config)
    {
        var header = $"target: {target}  enabled: {config.Enabled.ToString().ToLowerInvariant()}  concurrency: {config.Concurrency}  scope:";
        return config.Scope.Count == 0
            ? header + " (none)"
            : header + "\n" + string.Join('\n', config.Scope.Select(path => "  " + path));
    }
}
