using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Embedding.Download;

/// <summary>One entry of a Hugging Face tree listing (expand=true).</summary>
public sealed record HfTreeEntry(string Path, string Type, long Size, string? LfsOid);

/// <summary>The HF API refused or misbehaved: missing repo/revision, non-success status, or a malformed listing.</summary>
public sealed class HfApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
///     Resolves a repo's file tree via <c>GET /api/models/&lt;repo&gt;/tree/&lt;rev&gt;?recursive=true&amp;expand=true</c>
///     (plan §8.2 step 1): <c>expand=true</c> makes the API return the LFS <c>oid</c> (SHA-256)
///     per LFS file, which is captured BEFORE any download so the pin is never self-referential
///     (D8). Pagination follows the <c>Link: rel="next"</c> header past the 1000-entry default.
/// </summary>
public sealed class HfTreeClient
{
    private static readonly Regex NextLinkPattern = new("<([^>]+)>;\\s*rel=\"next\"", RegexOptions.Compiled);

    private readonly HttpClient _http;

    /// <summary>The shared client (downloads and raw fetches go through the same handler).</summary>
    internal HttpClient Http => _http;

    public string Endpoint { get; }

    public HfTreeClient(HttpClient http, string endpoint = "https://huggingface.co")
    {
        _http = http;
        Endpoint = endpoint.TrimEnd('/');
    }

    public async Task<IReadOnlyList<HfTreeEntry>> GetTreeAsync(string repoId, string revision, CancellationToken cancellationToken)
    {
        var entries = new List<HfTreeEntry>();
        var url = $"{Endpoint}/api/models/{repoId}/tree/{revision}?recursive=true&expand=true";
        while (url is not null)
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HfApiException(
                    $"could not resolve '{repoId}' at revision '{revision}' on Hugging Face (HTTP {(int)response.StatusCode}). " +
                    "Check the repo id and revision, then retry.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            entries.AddRange(ParseEntries(json, repoId, revision));

            url = NextLink(response);
        }

        return entries;
    }

    private static IEnumerable<HfTreeEntry> ParseEntries(string json, string repoId, string revision)
    {
        List<TreeEntryDto>? dto;
        try
        {
            dto = JsonSerializer.Deserialize<List<TreeEntryDto>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new HfApiException($"the tree listing for '{repoId}' at '{revision}' is not valid JSON", ex);
        }

        return (dto ?? []).Select(e => new HfTreeEntry(e.Path ?? string.Empty, e.Type ?? "file", e.Size, e.Lfs?.Oid));
    }

    /// <summary>Follows <c>&lt;url&gt;; rel="next"</c> — the HF pagination contract for trees &gt; 1000 entries.</summary>
    private static string? NextLink(HttpResponseMessage response)
    {
        var link = response.Headers.TryGetValues("Link", out var values) ? string.Join(",", values) : null;
        if (string.IsNullOrEmpty(link))
        {
            return null;
        }

        var match = NextLinkPattern.Match(link);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class TreeEntryDto
    {
        public string? Path { get; set; }

        public string? Type { get; set; }

        public long Size { get; set; }

        public LfsDto? Lfs { get; set; }
    }

    private sealed class LfsDto
    {
        public string? Oid { get; set; }
    }
}
