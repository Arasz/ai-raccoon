using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using AiRaccoon.Infrastructure.Common;

namespace AiRaccoon.Infrastructure.Provisioning;

public sealed class ExtensionProvisioningException(string message) : Exception(message);

/// <summary>Absolute paths of the provisioned loadable extension modules.</summary>
public sealed record ProvisionedExtensions(string? CloudSync);

/// <summary>
///     Downloads and verifies the pinned cloudsync module into &lt;dataRoot&gt;/extensions/&lt;rid&gt;/
///     (spec §10, OQ-3). Memory/vector natives are no longer provisioned (P1); P10 deletes this
///     provisioner once P9's own sync lands.
/// </summary>
public sealed class ExtensionProvisioner
{
    private readonly HttpClient _http;
    private readonly bool _includeCloudSync;
    private readonly string _rid;
    private readonly Func<string, string?> _sha256ForAsset;

    public ExtensionProvisioner(
        string dataRoot, string rid, HttpClient http, Func<string, string?> sha256ForAsset,
        bool includeCloudSync = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);

        ExtensionDirectory = dataRoot;
        _rid = rid;
        _http = http;
        _sha256ForAsset = sha256ForAsset;
        _includeCloudSync = includeCloudSync;
    }

    public string ExtensionDirectory => Path.Combine(field, "extensions", _rid);

    public async Task<ProvisionedExtensions> EnsureProvisionedAsync(CancellationToken cancellationToken = default)
    {
        if (!RuntimePlatform.TryGetSupportedAssetPlatform(_rid, out var platform))
        {
            throw MissingPlatformError();
        }

        Directory.CreateDirectory(ExtensionDirectory);

        foreach (var spec in RequiredSpecs())
        {
            var modulePath = ModulePath(spec);
            if (!File.Exists(modulePath))
            {
                await DownloadAndVerifyAsync(spec, platform, modulePath, cancellationToken).ConfigureAwait(false);
            }
        }

        return new ProvisionedExtensions(_includeCloudSync ? ModulePath(ExtensionCatalog.Sync) : null);
    }

    private List<ExtensionSpec> RequiredSpecs()
    {
        return _includeCloudSync ? [ExtensionCatalog.Sync] : [];
    }

    private string ModulePath(ExtensionSpec spec) => Path.Combine(ExtensionDirectory, spec.ModulePrefix + RuntimePlatform.ModuleExtension(_rid));

    private async Task DownloadAndVerifyAsync(ExtensionSpec spec, string platform, string modulePath,
        CancellationToken cancellationToken)
    {
        var asset = spec.AssetFileName(platform);
        var expectedSha256 = _sha256ForAsset(asset);
        if (expectedSha256 is not null)
        {
            var bytes = await _http.GetByteArrayAsync(spec.AssetUrl(platform), cancellationToken).ConfigureAwait(false);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtensionProvisioningException(
                    $"Checksum mismatch for '{asset}': expected {expectedSha256}, got {actualSha256}.");
            }

            Extract(bytes, asset, modulePath);
        }
        else
        {
            throw new ExtensionProvisioningException(
                $"No checksum recorded for '{asset}'; record its SHA-256 in ExtensionManifest before provisioning.");
        }
    }

    private void Extract(byte[] archive, string asset, string modulePath)
    {
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var tar = new MemoryStream();
        gzip.CopyTo(tar);
        tar.Position = 0;
        TarFile.ExtractToDirectory(tar, ExtensionDirectory, true);

        var moduleName = Path.GetFileName(modulePath);
        var modulePrefix = Path.GetFileNameWithoutExtension(moduleName);
        var extracted = Directory.EnumerateFiles(ExtensionDirectory)
            .FirstOrDefault(file =>
                Path.GetFileNameWithoutExtension(file).StartsWith(modulePrefix, StringComparison.Ordinal));

        switch (extracted)
        {
            case null:
                throw new ExtensionProvisioningException(
                    $"Archive '{asset}' did not contain a '{modulePrefix}' module.");
            default:
            {
                if (!string.Equals(Path.GetFileName(extracted), moduleName, StringComparison.Ordinal))
                {
                    File.Move(extracted, modulePath, true);
                }

                break;
            }
        }
    }

    private ExtensionProvisioningException MissingPlatformError()
    {
        var platform = RuntimePlatform.ExpectedAssetPlatform(_rid);
        var assets = string.Join(", ", RequiredSpecs().Select(spec => spec.AssetFileName(platform)));
        return new ExtensionProvisioningException(
            $"No sqliteai release binaries for RID '{_rid}'. Missing: {assets}. Provision the extensions manually into {ExtensionDirectory} or drop the RID (OQ-3).");
    }
}
