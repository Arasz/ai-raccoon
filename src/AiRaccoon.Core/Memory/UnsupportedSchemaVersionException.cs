namespace AiRaccoon.Core.Memory;

/// <summary>
///     The bank's stored schema version is newer than this binary supports. Thrown instead of
///     silently no-oping, which lets an old binary open a newer bank and write through paths that
///     skip that schema's maintenance.
/// </summary>
public sealed class UnsupportedSchemaVersionException(string message) : InvalidOperationException(message);
