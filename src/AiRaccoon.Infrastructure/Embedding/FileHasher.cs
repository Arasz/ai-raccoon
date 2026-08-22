using System.Security.Cryptography;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Hashes a file's bytes on disk (D1/D2: the one sha256 mechanism the manifest pin
/// verification and the download writer both use).</summary>
public interface IFileHasher
{
    /// <summary>Lowercase hex SHA-256 of the file's current bytes.</summary>
    string Sha256OfFile(string path);
}

public sealed class FileHasher : IFileHasher
{
    public string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
