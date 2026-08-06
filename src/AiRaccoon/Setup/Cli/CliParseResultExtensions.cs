namespace AiRaccoon.Setup.Cli;

public static class CliParseResultExtensions
{
    extension(CliParseResult result)
    {
        /// <summary>
        ///     Renders help, version, or parse errors to the given writer and returns the exit
        ///     code (0 help/version, 1 parse errors). Program.cs passes Console.Error.
        /// </summary>
        internal int RenderTo(TextWriter output) => CliRendering.Render(result, output);
    }
}
