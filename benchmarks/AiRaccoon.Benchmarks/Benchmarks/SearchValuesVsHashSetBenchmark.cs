using System.Buffers;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;

namespace AiRaccoon.Benchmarks.Benchmarks;

/// <summary>
///     Measures the per-token membership checks of FtsQueryNormalizer.BuildPlan (plan C
///     Wave 1): every query token is tested against the Reserved set, then the Stopwords
///     set for survivors. The workload is the real baseline query corpus
///     (scripts/baseline-queries.json, 35 queries) tokenized exactly like production
///     (lowercased, then filtered), so hit/miss ratios match live queries.
///     Arms: status-quo HashSet vs SearchValues, each under the comparer production uses
///     (OrdinalIgnoreCase) and under Ordinal (semantically identical here because tokens
///     are lowercased before the checks — informs whether the comparer itself costs).
///     Pipeline_* arms run the full tokenization path (regex + lowercase + filters) over
///     the 35 real query texts to show the end-to-end value of any lookup gain.
/// </summary>
[MemoryDiagnoser]
public partial class SearchValuesVsHashSetBenchmark
{
    // Mirrors FtsQueryNormalizer.Reserved / .Stopwords verbatim.
    private static readonly string[] ReservedWords = ["and", "or", "not", "near"];

    private static readonly string[] Stopwords =
    [
        "what", "is", "the", "how", "does", "about", "are", "do",
        "can", "should", "will", "would", "could", "has", "have", "been",
        "was", "were", "being", "a", "an", "in", "on", "at", "to",
        "for", "of", "by", "with", "from"
    ];

    // 207 tokens from the 35 real baseline queries, lowercased (as BuildPlan does).
    private static readonly string[] QueryTokens =
    [
        "why", "was", "shadcn", "ui", "chosen", "over", "gluestack", "io",
        "what", "adr", "governs", "uuid", "choice", "how", "does", "the",
        "project", "handle", "offer", "page", "fetching", "security", "what", "happened",
        "to", "the", "mcp", "server", "what", "replaced", "the", "llm",
        "cost", "nfr", "how", "does", "the", "project", "handle", "data",
        "erasure", "what", "is", "adr", "0070", "about", "what", "does",
        "adr", "0011", "decide", "what", "are", "the", "core", "architectural",
        "principles", "what", "azure", "services", "does", "the", "project", "use",
        "how", "does", "local", "development", "work", "what", "is", "the",
        "cosmos", "partition", "key", "strategy", "what", "are", "the", "extension",
        "points", "how", "does", "authentication", "work", "is", "tdd", "required",
        "what", "is", "the", "screaming", "architecture", "rule", "how", "should",
        "nuget", "packages", "be", "managed", "what", "logging", "pattern", "is",
        "required", "are", "hardcoded", "secrets", "allowed", "what", "error", "format",
        "does", "the", "api", "use", "how", "does", "the", "task",
        "orchestration", "skill", "work", "what", "agents", "are", "available", "what",
        "model", "does", "the", "architect", "use", "how", "do", "prompt",
        "markers", "work", "what", "is", "ats", "001", "what", "are",
        "the", "three", "layers", "of", "modern", "hiring", "screens", "what",
        "ats", "rules", "govern", "keyword", "usage", "what", "major", "work",
        "happened", "in", "week", "of", "2026", "07", "27", "when",
        "was", "the", "project", "established", "what", "identity", "patterns", "does",
        "the", "project", "exhibit", "how", "is", "compensation", "calculated", "what",
        "is", "the", "frontend", "technology", "stack", "how", "does", "channel",
        "monitoring", "work", "what", "is", "in", "state", "json", "what",
        "happened", "today", "show", "me", "the", "aspire", "config",
    ];

    // The 35 real baseline queries verbatim, for the end-to-end Pipeline arms.
    private static readonly string[] QueryTexts =
    [
        "Why was shadcn/ui chosen over gluestack.io?", "What ADR governs UUID choice?", "How does the project handle offer-page fetching security?",
        "What happened to the MCP server?", "What replaced the LLM cost NFR?", "How does the project handle data erasure?",
        "What is ADR-0070 about?", "What does ADR-0011 decide?", "What are the core architectural principles?",
        "What Azure services does the project use?", "How does local development work?", "What is the Cosmos partition key strategy?",
        "What are the extension points?", "How does authentication work?", "Is TDD required?",
        "What is the screaming architecture rule?", "How should NuGet packages be managed?", "What logging pattern is required?",
        "Are hardcoded secrets allowed?", "What error format does the API use?", "How does the task orchestration skill work?",
        "What agents are available?", "What model does the architect use?", "How do prompt markers work?",
        "What is ATS-001?", "What are the three layers of modern hiring screens?", "What ATS rules govern keyword usage?",
        "What major work happened in week of 2026-07-27?", "When was the project established?", "What identity patterns does the project exhibit?",
        "How is compensation calculated?", "What is the frontend technology stack?", "How does channel monitoring work?",
        "What is in state.json?", "What happened today?", "Show me the Aspire config",
    ];

    private HashSet<string> _reservedHashSet = null!;
    private HashSet<string> _stopwordsHashSet = null!;
    private HashSet<string> _reservedHashSetOrdinal = null!;
    private HashSet<string> _stopwordsHashSetOrdinal = null!;
    private SearchValues<string> _reservedSearchValues = null!;
    private SearchValues<string> _stopwordsSearchValues = null!;
    private SearchValues<string> _reservedSearchValuesOrdinal = null!;
    private SearchValues<string> _stopwordsSearchValuesOrdinal = null!;

    // Same tokenizer as production FtsQueryNormalizer.TokenRegex.
    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GlobalSetup]
    public void Setup()
    {
        _reservedHashSet = new HashSet<string>(ReservedWords, StringComparer.OrdinalIgnoreCase);
        _stopwordsHashSet = new HashSet<string>(Stopwords, StringComparer.OrdinalIgnoreCase);
        _reservedHashSetOrdinal = new HashSet<string>(ReservedWords, StringComparer.Ordinal);
        _stopwordsHashSetOrdinal = new HashSet<string>(Stopwords, StringComparer.Ordinal);
        _reservedSearchValues = SearchValues.Create(ReservedWords, StringComparison.OrdinalIgnoreCase);
        _stopwordsSearchValues = SearchValues.Create(Stopwords, StringComparison.OrdinalIgnoreCase);
        _reservedSearchValuesOrdinal = SearchValues.Create(ReservedWords, StringComparison.Ordinal);
        _stopwordsSearchValuesOrdinal = SearchValues.Create(Stopwords, StringComparison.Ordinal);
    }

    [Benchmark(Baseline = true)]
    public int HashSet_OrdinalIgnoreCase()
    {
        var kept = 0;
        foreach (var token in QueryTokens)
        {
            if (!_reservedHashSet.Contains(token) && !_stopwordsHashSet.Contains(token))
            {
                kept++;
            }
        }

        return kept;
    }

    [Benchmark]
    public int SearchValues_OrdinalIgnoreCase()
    {
        var kept = 0;
        foreach (var token in QueryTokens)
        {
            if (!_reservedSearchValues.Contains(token) && !_stopwordsSearchValues.Contains(token))
            {
                kept++;
            }
        }

        return kept;
    }

    [Benchmark]
    public int HashSet_Ordinal()
    {
        var kept = 0;
        foreach (var token in QueryTokens)
        {
            if (!_reservedHashSetOrdinal.Contains(token) && !_stopwordsHashSetOrdinal.Contains(token))
            {
                kept++;
            }
        }

        return kept;
    }

    [Benchmark]
    public int SearchValues_Ordinal()
    {
        var kept = 0;
        foreach (var token in QueryTokens)
        {
            if (!_reservedSearchValuesOrdinal.Contains(token) && !_stopwordsSearchValuesOrdinal.Contains(token))
            {
                kept++;
            }
        }

        return kept;
    }

    // End-to-end: the whole BuildPlan tokenization pass (regex + lowercase + both
    // filters) over the 35 real query texts, per lookup backing store.
    [Benchmark]
    public int Pipeline_HashSet_OrdinalIgnoreCase()
    {
        var kept = 0;
        foreach (var query in QueryTexts)
        {
            foreach (Match match in TokenRegex().Matches(query))
            {
                var token = match.Value.ToLowerInvariant();
                if (!_reservedHashSet.Contains(token) && !_stopwordsHashSet.Contains(token))
                {
                    kept++;
                }
            }
        }

        return kept;
    }

    [Benchmark]
    public int Pipeline_SearchValues_Ordinal()
    {
        var kept = 0;
        foreach (var query in QueryTexts)
        {
            foreach (Match match in TokenRegex().Matches(query))
            {
                var token = match.Value.ToLowerInvariant();
                if (!_reservedSearchValuesOrdinal.Contains(token) && !_stopwordsSearchValuesOrdinal.Contains(token))
                {
                    kept++;
                }
            }
        }

        return kept;
    }
}
