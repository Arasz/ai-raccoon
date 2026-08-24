namespace AiRaccoon.Core.Memory.Code;

/// <summary>
///     The default code engine's identity and the one command that installs it (#422). Every
///     surface that can notice the code engine is missing — the search warning, `doctor`, the MCP
///     server instructions, the `memory_search` description, the how-to, the ai-badger skill —
///     quotes <see cref="DefaultModelCommand" /> verbatim rather than spelling a command of its own.
/// </summary>
public static class CodeEngineSetup
{
    /// <summary>The Hugging Face repo the code corpus's default embedding engine is published at.</summary>
    public const string DefaultModelRepoId = "faxenoff/code-daemon-embed-v1";

    /// <summary>Downloads <see cref="DefaultModelRepoId" /> if it is not already on disk, then activates it.</summary>
    public const string DefaultModelCommand = "ai-raccoon model code set default";
}
