namespace AiRaccoon.Core.Projects;

/// <summary>A project was registered or renamed to the reserved name <c>shared</c>; the MCP tools map this to <c>reserved-name</c>.</summary>
public sealed class ReservedProjectNameException()
    : InvalidOperationException(
        "'shared' is the reserved shared target; it needs no minted id and cannot be registered under another id. Nothing was registered.");
