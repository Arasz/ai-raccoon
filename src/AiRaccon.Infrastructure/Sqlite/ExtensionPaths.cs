using AiRaccon.Infrastructure.Common;
using AiRaccon.Infrastructure.Provisioning;

namespace AiRaccon.Infrastructure.Sqlite;

/// <summary>Absolute paths of the loadable extension modules for a platform.</summary>
public sealed record ExtensionPaths(string Vector, string Memory, string? CloudSync)
{
    public static ExtensionPaths For(string dataRoot, string rid, bool includeCloudSync)
    {
        var directory = Path.Combine(dataRoot, "extensions", rid);
        string Module(ExtensionSpec spec) => Path.Combine(directory, spec.ModulePrefix + RuntimePlatform.ModuleExtension(rid));

        return new ExtensionPaths(
            Module(ExtensionCatalog.Vector),
            Module(ExtensionCatalog.Memory),
            includeCloudSync ? Module(ExtensionCatalog.Sync) : null);
    }
}
