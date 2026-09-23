namespace AiRaccoon.Core.Memory.Code;

/// <summary>
///     The default code engine's identity and the one command that installs it (#422). Every
///     surface that can notice the code engine is missing — the search warning, `doctor`, the MCP
///     server instructions, the `memory_search` description, the how-to, the ai-badger skill —
///     quotes <see cref="DefaultModelCommand" /> verbatim rather than spelling a command of its own.
/// </summary>
public static class CodeEngineSetup
{
    /// <summary>Activates the bundled engine (ADR-0108) for the code corpus; nothing is downloaded.</summary>
    public const string DefaultModelCommand = "ai-raccoon model code set default";
}
