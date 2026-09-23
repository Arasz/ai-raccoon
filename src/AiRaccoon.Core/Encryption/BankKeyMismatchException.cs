namespace AiRaccoon.Core.Encryption;

/// <summary>
///     The bank did not open under the key derived for its encryption source. Carries the
///     underlying SQLite failure; see ADR-0012 and docs/plans/2026-08-07-hkdf-rekey-migration.md.
/// </summary>
public sealed class BankKeyMismatchException(string message, Exception? innerException = null, bool legacyDerivation = false)
    : InvalidOperationException(message, innerException)
{
    /// <summary>True when the bank opens under the pre-ADR-0012 derivation: 'encryption migrate' fixes it.</summary>
    public bool LegacyDerivation { get; } = legacyDerivation;
}
