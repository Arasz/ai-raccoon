using System.Text.Json;

namespace AiRaccoon.Tests.Integration;

/// <summary>The retrieval query catalog (scripts/baseline-queries.json) as the single source the
/// pinned-vector fixture and its coverage gate both derive from.</summary>
internal static class BaselineQueryCatalog
{
    public const string RelativePath = "scripts/baseline-queries.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<CatalogQuery> Load() =>
        JsonSerializer.Deserialize<CatalogQuery[]>(File.ReadAllText(TestData.RepoFile(RelativePath)), JsonOptions)
        ?? [];
}

internal sealed record CatalogQuery(
    string Id, string Category, string Query, string? ExpectedSource,
    string? ExpectedKnowledge, string Scope, int SearchLimit, bool NegativeTest);
