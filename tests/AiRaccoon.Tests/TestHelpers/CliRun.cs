using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Runs one CLI command in-process against captured streams: parses argv, hands the parse and
///     the streams to <paramref name="invoke" />, and returns its exit code with stdout/stderr.
/// </summary>
internal static class CliRun
{
    internal static async Task<(int Exit, string Out, string Err)> RunAsync(
        string[] args,
        Func<CliInput, StandardStreams, CancellationToken, Task<int>> invoke,
        TextReader? stdin = null)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await invoke(parsed, new StandardStreams(stdin ?? TextReader.Null, stdout, stderr),
            TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Dispatches through <see cref="ConfigCommands" />, the same entry point Program.cs uses.</summary>
    internal static Task<(int Exit, string Out, string Err)> RunAsync(
        string[] args, ConfigCommands commands, TextReader? stdin = null) =>
        RunAsync(args, (parsed, streams, cancellationToken) => commands.RunAsync(parsed, streams, cancellationToken),
            stdin);
}
