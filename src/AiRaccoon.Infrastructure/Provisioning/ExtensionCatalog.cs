namespace AiRaccoon.Infrastructure.Provisioning;

/// <summary>One pinned sqliteai extension: version, module prefix, and release asset template (spec §10).</summary>
public sealed record ExtensionSpec(
    string Key,
    string Repo,
    string Version,
    string ModulePrefix,
    string Flavor,
    string AssetTemplate)
{
    public string AssetFileName(string platform) =>
        AssetTemplate
            .Replace("{platform}", platform)
            .Replace("{version}", Version)
            .Replace("{flavor}", Flavor);

    public Uri AssetUrl(string platform) => new($"https://github.com/sqliteai/{Repo}/releases/download/{Version}/{AssetFileName(platform)}");
}

/// <summary>
///     Pinned sqliteai extension versions and their GitHub release asset naming. P1 provisions
///     sync only (memory/vector natives are gone — vec0 now comes from the NuGet package); P10
///     deletes the provisioner entirely.
/// </summary>
public static class ExtensionCatalog
{
    // ModulePrefix is the loadable-module basename (without platform extension): SQLite derives
    // the entry point sqlite3_<basename>_init from it, so it must match the archive's module
    // name exactly (cloudsync -> sqlite3_cloudsync_init).
    public static readonly ExtensionSpec Sync = new(
        "sync", "sqlite-sync", "1.1.2", "cloudsync", "", "cloudsync-{platform}-{version}.tar.gz");
}
