using System.Text.Json;
using AiRaccoon.Core.Encryption;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

/// <summary>The encryption source sidecar exists but cannot be read.</summary>
public sealed class EncryptionSourceException(string message) : InvalidOperationException(message);

/// <summary>
///     Reads/writes the <c>memory.db.source</c> sidecar next to the bank. Absence means the env
///     source; a corrupt sidecar fails loudly with the path; writes are atomic (temp + rename).
/// </summary>
public sealed class EncryptionState : IEncryptionState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;

    public EncryptionState(string encryptionDataFilePath)
    {
        Guard.IsNotNullOrWhiteSpace(encryptionDataFilePath);
        _path = PathFor(encryptionDataFilePath);
    }

    public EncryptionData Read()
    {
        if (!File.Exists(_path))
        {
            return EncryptionData.None;
        }

        EncryptionData config;
        try
        {
            config = JsonSerializer.Deserialize<EncryptionData>(File.ReadAllText(_path), JsonOptions)
                     ?? throw new JsonException("the sidecar is empty");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Corrupt(ex.Message);
        }

        return config.Source is not ("env" or "bitwarden") ? throw Corrupt($"unknown source '{config.Source}'") : config;
    }

    public void Write(EncryptionData config)
    {
        Guard.IsNotNull(config);
        if (config.Source is not ("env" or "bitwarden"))
        {
            throw new ArgumentException($"source must be \"env\" or \"bitwarden\", was \"{config.Source}\"", nameof(config));
        }

        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(config, JsonOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tempPath, _path, true);
    }

    public void Delete() => File.Delete(_path);

    public static string PathFor(string encryptionDataFilePath) => $"{encryptionDataFilePath}.source";

    private EncryptionSourceException Corrupt(string reason) => new($"encryption source sidecar '{_path}' is corrupt: {reason}");
}
