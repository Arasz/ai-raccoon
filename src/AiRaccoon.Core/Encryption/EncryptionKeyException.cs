namespace AiRaccoon.Core.Encryption;

/// <summary>Base for errors rejecting an encryption key source value (plan §5.1).</summary>
public abstract class EncryptionKeyException(string message) : InvalidOperationException(message);

/// <summary>Raised when the key source is an OpenSSH key encrypted with a passphrase (ciphername ≠ "none").</summary>
public sealed class PassphraseProtectedKeyException() : EncryptionKeyException("passphrase-protected keys are not supported");

/// <summary>Raised when the key source is not an ed25519 key (e.g. ssh-rsa).</summary>
public sealed class UnsupportedKeyTypeException() : EncryptionKeyException("only ed25519 keys are supported");

/// <summary>Raised when the key source does not decode as a valid unencrypted openssh-key-v1 blob.</summary>
public sealed class MalformedPrivateKeyException(string detail) : EncryptionKeyException($"malformed OpenSSH private key: {detail}");
