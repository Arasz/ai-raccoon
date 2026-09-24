using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

/// <summary>
///     Marks a bank as mid-rekey (ADR-0111 D6). Held from just before <c>PRAGMA rekey</c> to just
///     after the key-check sidecar is rewritten for the new key, so a concurrent opener still on the
///     old key — SQLITE_NOTADB against a sidecar that has not been rewritten yet — is diagnosed as
///     ambiguous instead of confidently "corrupt": the sidecar may still be describing the key the
///     rekey is replacing, not the on-disk reality this open just hit.
/// </summary>
public sealed class RekeyMarker(string bankPath)
{
    public const string Suffix = ".rekeying";

    public string FilePath { get; } = PathFor(bankPath);

    public bool IsPresent => File.Exists(FilePath);

    public static string PathFor(string bankPath)
    {
        Guard.IsNotNullOrWhiteSpace(bankPath);
        return $"{bankPath}{Suffix}";
    }

    public void Mark()
    {
        using var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    /// <summary>Idempotent: clearing an already-absent marker is not an error.</summary>
    public void Clear() => File.Delete(FilePath);
}
