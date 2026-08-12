using System.Text.Json;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

/// <summary>The encryption source sidecar exists but cannot be read.</summary>
public sealed class EncryptionSourceException(string message) : InvalidOperationException(message);

/// <summary>
///     Reads/writes the <c>memory.db.source</c> sidecar next to the bank. Absence means the env
///     source; a corrupt sidecar fails loudly with the path; writes are atomic (temp + rename).
/// </summary>
public sealed class EncryptionSourceSidecar : IEncryptionSourceSidecar
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public EncryptionSourceSidecar(string encryptionDataFilePath)
    {
        Guard.IsNotNullOrWhiteSpace(encryptionDataFilePath);
        FilePath = PathFor(encryptionDataFilePath);
    }

    public string FilePath { get; }

    public EncryptionData Read()
    {
        if (!File.Exists(FilePath))
        {
            return EncryptionData.None;
        }

        EncryptionData config;
        try
        {
            config = JsonSerializer.Deserialize<EncryptionData>(File.ReadAllText(FilePath), JsonOptions)
                     ?? throw new JsonException("the sidecar is empty");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Corrupt(ex.Message);
        }

        return config.Source is not (EnvEncryptionKeyProvider.EncryptionSource or BitwardenEncryptionKeyProvider.EncryptionSource)
            ? throw Corrupt($"unknown source '{config.Source}'")
            : config;
    }

    public void Write(EncryptionData config)
    {
        Guard.IsNotNull(config);
        if (config.Source is not (EnvEncryptionKeyProvider.EncryptionSource or BitwardenEncryptionKeyProvider.EncryptionSource))
        {
            throw new ArgumentException($"source must be \"env\" or \"bitwarden\", was \"{config.Source}\"", nameof(config));
        }

        var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(config, JsonOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tempPath, FilePath, true);
    }

    public void Delete() => File.Delete(FilePath);

    public static string PathFor(string encryptionDataFilePath) => $"{encryptionDataFilePath}.source";

    private EncryptionSourceException Corrupt(string reason) => new($"encryption source sidecar '{FilePath}' is corrupt: {reason}");
}
