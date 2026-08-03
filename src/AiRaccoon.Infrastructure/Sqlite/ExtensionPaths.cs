using AiRaccoon.Infrastructure.Common;
using AiRaccoon.Infrastructure.Provisioning;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Absolute path of the loadable cloudsync module for a platform. Vector/memory natives are
///     no longer provisioned (P1); sync stays extension-backed until P9 replaces it and P10
///     deletes the provisioner.
/// </summary>
public static class ExtensionPaths
{
    public static string CloudSyncModulePath(string dataRoot, string rid) =>
        Path.Combine(dataRoot, "extensions", rid,
            $"{ExtensionCatalog.Sync.ModulePrefix}{RuntimePlatform.ModuleExtension(rid)}");
}
