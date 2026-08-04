namespace AiRaccoon.Core.Memory;

/// <summary>Outcome of a bank settings write via the MCP tools.</summary>
public sealed record SettingResult(string Key, string Value);
