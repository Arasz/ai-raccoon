namespace AiRaccon.Infrastructure.Provisioning;

/// <summary>One pinned sqliteai extension: version, module prefix, and release asset template (spec §10).</summary>
public sealed record ExtensionSpec(
    string Key,
    string Repo,
    string Version,
    string ModulePrefix,
    string Flavor,
    string AssetTemplate)
{
    public string AssetFileName(string platform) => AssetTemplate
        .Replace("{platform}", platform)
        .Replace("{version}", Version)
        .Replace("{flavor}", Flavor);

    public Uri AssetUrl(string platform) =>
        new($"https://github.com/sqliteai/{Repo}/releases/download/{Version}/{AssetFileName(platform)}");
}

/// <summary>Pinned sqliteai extension versions and their GitHub release asset naming.</summary>
public static class ExtensionCatalog
{
    public static readonly ExtensionSpec Vector = new(
        "vector", "sqlite-vector", "1.0.0", "vector0", "", "vector-{platform}-{version}.tar.gz");

    public static readonly ExtensionSpec Memory = new(
        "memory", "sqlite-memory", "1.3.5", "memory0", "full", "memory-{platform}-{flavor}-{version}.tar.gz");

    public static readonly ExtensionSpec Sync = new(
        "sync", "sqlite-sync", "1.1.2", "sync0", "", "cloudsync-{platform}-{version}.tar.gz");
}
