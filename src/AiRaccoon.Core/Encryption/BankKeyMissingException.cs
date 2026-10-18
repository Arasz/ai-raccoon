namespace AiRaccoon.Core.Encryption;

/// <summary>
///     The bank is encrypted, but no encryption key was resolved to open it with. Carries the
///     underlying SQLite failure; the message names the key source to set.
/// </summary>
public sealed class BankKeyMissingException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
