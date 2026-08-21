using AiRaccoon.Core.Watch;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     Reads and parses `&lt;root&gt;/ai-raccoon.ignore` (docs/work/2026-08-21-code-search-implementation-plan.md
///     §2.1/§5.2): one file per watch/ingest root, never nested discovery. Reads fresh on every
///     call — no cache — so the watch and ingest layers stay the single source of "when to read".
/// </summary>
public interface IIgnoreRulesProvider
{
    Task<IgnoreRules> LoadAsync(string root, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IIgnoreRulesProvider" />
public sealed class IgnoreRulesProvider : IIgnoreRulesProvider
{
    public const string FileName = "ai-raccoon.ignore";

    public async Task<IgnoreRules> LoadAsync(string root, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return IgnoreRules.Empty;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return IgnoreRules.Parse(content);
    }
}

/// <summary>Null object for callers that construct their dependency without DI (test/legacy call
/// sites keeping a positional constructor working) — resolves to no rules, ever.</summary>
public sealed class NullIgnoreRulesProvider : IIgnoreRulesProvider
{
    public static NullIgnoreRulesProvider Instance { get; } = new();

    public Task<IgnoreRules> LoadAsync(string root, CancellationToken cancellationToken = default) => Task.FromResult(IgnoreRules.Empty);
}
