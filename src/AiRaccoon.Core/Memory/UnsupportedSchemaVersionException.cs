namespace AiRaccoon.Core.Memory;

/// <summary>
///     The bank's stored schema version is newer than this binary supports. Thrown instead of
///     silently no-oping, which is how issue #200's drift happened: an old binary opened a newer
///     bank and wrote through write paths that skipped that schema's maintenance.
/// </summary>
public sealed class UnsupportedSchemaVersionException(string message) : InvalidOperationException(message);
