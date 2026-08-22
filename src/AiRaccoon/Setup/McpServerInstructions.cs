using AiRaccoon.Core.Memory.Code;

namespace AiRaccoon.Setup;

/// <summary>
///     The server's MCP <c>instructions</c> string: what this server is, and the one thing a client
///     cannot work out from a tool schema — that code search silently degrades to keyword-only until
///     its engine is installed (#422), and the exact command that installs it.
/// </summary>
internal static class McpServerInstructions
{
    public const string Text =
        "AiRaccoon is this project's memory bank: hybrid keyword + semantic search over notes you " +
        "write, and over the project's own source code. Search it before web search, repo search, or " +
        "asking the user; every call is scoped to a projectId.\n\n" +
        "The code corpus has its OWN embedding engine, and a fresh install does not have it. When a " +
        "memory_search result (kind=code or kind=both) carries the warning \"" +
        CodeSearchWarnings.EngineNotConfiguredPrefix + "\", the code section came from keyword " +
        "matching alone: semantic code matches are missing, not absent. Tell the user once, verbatim, " +
        "to run '" + CodeEngineSetup.DefaultModelCommand + "' — it downloads and activates the default " +
        "code embedding model — and say that code results were keyword-only until then. Do not re-run " +
        "the search hoping for vectors; nothing changes until that command runs.";
}
