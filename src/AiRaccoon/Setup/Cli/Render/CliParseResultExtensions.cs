namespace AiRaccoon.Setup.Cli.Render;

public static class CliParseResultExtensions
{
    extension(CliInput result)
    {
        /// <summary>
        ///     Renders help, version, or parse errors to the given writer and returns the exit
        ///     code (0 help/version, 1 parse errors). Program.cs passes Console.Error.
        /// </summary>
        internal void RenderTo(StandardStreams streams) => CliRendering.Render(result, streams);
    }
}
