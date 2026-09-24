namespace AiRaccoon.Core.Encryption;

/// <summary>
///     The bank does not open, and its key verifier proves the resolved key is right — so the file
///     itself is damaged, not the key. See ADR-0111.
/// </summary>
public sealed class BankCorruptedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
