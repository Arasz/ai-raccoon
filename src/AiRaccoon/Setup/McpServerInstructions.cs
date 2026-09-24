using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;

namespace AiRaccoon.Setup;

/// <summary>
///     The server's MCP <c>instructions</c> string: what this server is, and the one thing a client
///     cannot work out from a tool schema — that memory and code search silently degrade to
///     keyword-only until their engines are installed (#422, F6), and the exact command that
///     installs each.
/// </summary>
internal static class McpServerInstructions
{
    public const string Text =
        "AiRaccoon is this project's memory bank: hybrid keyword + semantic search over notes you " +
        "write, and over the project's own source code. Search it before web search, repo search, or " +
        "asking the user; every call is scoped to a projectId.\n\n" +
        "Memory and code have independent embedding engines, and a fresh install has neither. When a " +
        "memory_search result carries the warning \"" + SearchWarnings.EngineNotConfiguredPrefix + "\", " +
        "the memory section came from keyword matching alone: semantic memory matches are missing, not " +
        "absent. Tell the user once, verbatim, to run '" + EmbeddingEngineSetup.DefaultModelCommand + "' " +
        "— it activates the embedding model bundled with the tool — and say that memory results " +
        "were keyword-only until then. When it carries \"" + CodeSearchWarnings.EngineNotConfiguredPrefix + "\", " +
        "the same is true of the code section, whose engine is installed by '" +
        CodeEngineSetup.DefaultModelCommand + "'. Do not re-run the search hoping for vectors; nothing " +
        "changes until that command runs.";
}
