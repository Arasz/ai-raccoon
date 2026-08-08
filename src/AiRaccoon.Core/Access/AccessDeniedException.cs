namespace AiRaccoon.Core.Access;

/// <summary>The project's access mode does not allow the requirement — the MCP tool maps this to `access-denied`.</summary>
public sealed class AccessDeniedException(string message) : InvalidOperationException(message);
