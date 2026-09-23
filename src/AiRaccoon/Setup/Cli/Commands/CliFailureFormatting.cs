namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>UX-F7: reformats common I/O failures against --data-root into an actionable
/// sentence instead of a raw OS errno string; anything unrecognized falls back to ex.Message.</summary>
internal static class CliFailureFormatting
{
    internal static string Format(Exception ex, string dataRoot) =>
        ex switch
        {
            UnauthorizedAccessException => $"ai-raccoon: permission denied for --data-root '{dataRoot}' — check the directory's permissions, or pass a different --data-root",
            IOException io when IsReadOnly(io) =>
                $"ai-raccoon: --data-root '{dataRoot}' is on a read-only filesystem — pass a writable --data-root",
            IOException io when IsTooLong(io) =>
                $"ai-raccoon: --data-root '{dataRoot}' is too long for this filesystem — pass a shorter --data-root",
            _ => $"ai-raccoon: {ex.Message}"
        };

    /// <summary>True for the failures <see cref="Format" /> reports as an unusable --data-root value.</summary>
    internal static bool BlamesDataRoot(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException io && (IsReadOnly(io) || IsTooLong(io)));

    private static bool IsReadOnly(IOException io) => io.Message.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase);

    private static bool IsTooLong(IOException io) => io.Message.Contains("too long", StringComparison.OrdinalIgnoreCase);
}
