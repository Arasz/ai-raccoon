using System.Text.Json;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Encryption source persisted in the sidecar: "env" or "bitwarden".</summary>
public sealed record EncryptionSourceConfig(string Source, string? ProjectId, string? SecretId);

/// <summary>The encryption source sidecar exists but cannot be read.</summary>
public sealed class EncryptionSourceException(string message) : InvalidOperationException(message);

/// <summary>
///     Reads/writes the <c>memory.db.source</c> sidecar next to the bank. Absence means the env
///     source; a corrupt sidecar fails loudly with the path; writes are atomic (temp + rename).
/// </summary>
public sealed class EncryptionSourceSidecar
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;

    public EncryptionSourceSidecar(string bankPath)
    {
        Guard.IsNotNullOrWhiteSpace(bankPath);
        _path = PathFor(bankPath);
    }

    public static string PathFor(string bankPath) => bankPath + ".source";

    public EncryptionSourceConfig? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        EncryptionSourceConfig config;
        try
        {
            config = JsonSerializer.Deserialize<EncryptionSourceConfig>(File.ReadAllText(_path), JsonOptions)
                ?? throw new JsonException("the sidecar is empty");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Corrupt(ex.Message);
        }

        if (config.Source is not ("env" or "bitwarden"))
        {
            throw Corrupt($"unknown source '{config.Source}'");
        }

        return config;
    }

    public void Write(EncryptionSourceConfig config)
    {
        Guard.IsNotNull(config);
        if (config.Source is not ("env" or "bitwarden"))
        {
            throw new ArgumentException($"source must be \"env\" or \"bitwarden\", was \"{config.Source}\"", nameof(config));
        }

        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(config, JsonOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tempPath, _path, overwrite: true);
    }

    public void Delete() => File.Delete(_path);

    private EncryptionSourceException Corrupt(string reason) =>
        new($"encryption source sidecar '{_path}' is corrupt: {reason}");
}
