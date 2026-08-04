using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class RetrievalBaselineTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] IncludeGlobs =
    [
        "docs/adr/*.md", "docs/*.md", "docs/explanation/*.md", "docs/how-to/*.md",
        "docs/reference/*.md", "docs/rules/*.md", "docs/tutorials/*.md", "docs/legacy/*.md",
        "docs/meta/baseline.json", "docs/meta/trust-debt.md", "docs/meta/trust-index.json",
        ".ai-badger/invariants/*.md", ".ai-badger/skills/*/SKILL.md",
        ".ai-badger/agents/*.md", ".ai-badger/instructions/*.md",
        ".ai-badger/config.json", ".ai-badger/delegation.md", ".ai-badger/copilot-instructions.md",
        ".ai-badger/agent-instructions/*",
        ".remember/recent.md", ".remember/archive.md",
        "README.md", "CLAUDE.md", "HERMES.md", "REVIEW.md", "infra/README.md"
    ];

    private static readonly string[] ExcludeGlobs =
    [
        ".ai-badger/state.json", ".ai-badger/status-notes.json",
        ".remember/now.md", ".remember/today-*.md",
        "docs/work/**", "docs/brand/**", ".github/**"
    ];

    private readonly string _dataRoot;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SqliteMemoryStore _store;
    private readonly ITestOutputHelper _output;

    public RetrievalBaselineTests(ITestOutputHelper output)
    {
        _output = output;
        _dataRoot = CreateTempRoot();
        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
        _store = new SqliteMemoryStore(factory, new FakeTimeProvider(FixedNow),
            new TokenizerChunker(),
            new EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task RunAllBaselineQueries_ReportsMatchStatistics()
    {
        var queries = LoadQueries();
        queries.ShouldNotBeEmpty("baseline-queries.json should contain queries");
        _output.WriteLine($"Loaded {queries.Length} baseline queries");

        var chunkMap = await SeedStoreAsync();
        _output.WriteLine($"Seeded {chunkMap.Count} chunks into memory.db");

        var scored = new List<QueryResult>();
        var matchesAtTop3 = 0;
        var totalWithResults = 0;

        foreach (var query in queries)
        {
            var searchQuery = new SearchQuery("jsaa", query.Query, SearchScope.Project,
                Limit: query.SearchLimit, MinScore: 0.0);
            var results = await _store.SearchAsync(searchQuery, TestContext.Current.CancellationToken);

            var mappedResults = results.Select((r, i) => new ResultEntry(
                i + 1, r.Hash, r.Ranking,
                r.Path, r.Snippet,
                query.ExpectedSource is not null
                && chunkMap.TryGetValue(query.ExpectedSource, out var expectedHash)
                && r.Hash == expectedHash
            )).ToList();

            if (mappedResults.Count > 0)
            {
                totalWithResults++;
            }

            if (!query.NegativeTest && mappedResults.Take(3).Any(r => r.IsExpectedSource))
            {
                matchesAtTop3++;
            }

            scored.Add(new QueryResult(query.Id, query.Category, query.Query,
                query.ExpectedSource, query.ExpectedKnowledge, mappedResults));
        }

        var nonNegative = queries.Count(q => !q.NegativeTest);
        _output.WriteLine($"Results: {totalWithResults}/{queries.Length} queries returned results");
        _output.WriteLine($"Expected-source matches at rank ≤3: {matchesAtTop3}/{nonNegative}");

        var baseline = new BaselineReport("jsaa", DateTimeOffset.UtcNow,
            queries.Length, totalWithResults, matchesAtTop3, scored);
        var reportPath = Path.Combine(_dataRoot, "scored-baseline.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(baseline, _jsonOptions),
            TestContext.Current.CancellationToken);
        _output.WriteLine($"Baseline written to {reportPath}");

        // Also copy to project root for external analysis
        var projectRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var copyPath = Path.Combine(projectRoot, "scored-baseline.json");
        File.Copy(reportPath, copyPath, overwrite: true);
        _output.WriteLine($"Also copied to {copyPath}");

        matchesAtTop3.ShouldBeGreaterThanOrEqualTo(1,
            "at least one expected source should match at rank ≤3 after seeding");
    }

    private async Task<Dictionary<string, string>> SeedStoreAsync()
    {
        var jsaaRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "RiderProjects", "job-search-ai-assistant");

        if (!Directory.Exists(jsaaRoot))
        {
            _output.WriteLine($"JSAA project not found at {jsaaRoot} — seeding minimal test data");
            return await SeedMinimalTestDataAsync();
        }

        var chunkMap = new Dictionary<string, string>();
        var files = EnumerateFiles(jsaaRoot);
        _output.WriteLine($"Found {files.Count} files to seed");

        foreach (var (relPath, fileType) in files)
        {
            var fullPath = Path.Combine(jsaaRoot, relPath);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(fullPath, TestContext.Current.CancellationToken);
            var chunks = ChunkFile(relPath, fileType, content);

            foreach (var (structuredPath, context, chunkContent) in chunks)
            {
                var contentWithMeta = $"[{context}] {structuredPath}\n\n{chunkContent}";
                var request = new MemoryWriteRequest("jsaa", contentWithMeta, AgentId: structuredPath);
                var entry = await _store.WriteAsync(request, TestContext.Current.CancellationToken);
                var expectedHash = ComputeExpectedHash(contentWithMeta);
                chunkMap[structuredPath] = expectedHash;
            }
        }

        await _store.ConfigureEmbeddingAsync("jsaa", "local", null, null, null,
            TestContext.Current.CancellationToken);
        await _store.EmbedPendingAsync("jsaa", null, TestContext.Current.CancellationToken);

        var stats = await _store.GetStatsAsync("jsaa", TestContext.Current.CancellationToken);
        _output.WriteLine($"Seeded {stats.EntryCount} entries, {stats.PendingCount} pending");
        return chunkMap;
    }

    private async Task<Dictionary<string, string>> SeedMinimalTestDataAsync()
    {
        var chunkMap = new Dictionary<string, string>();
        var docs = new (string Path, string Context, string Content)[]
        {
            ("ai-badger:invariants/tdd-mandatory.md", "docs:invariants",
                "# TDD Mandatory\n\nWrite a failing, behavior-focused test before any production code change."),
            ("docs:adr:0001-sample.md#decision", "docs:adr",
                "## Decision\n\nUse FluentValidation for all input validation in the domain layer."),
            ("docs:architecture:architecture.md#overview", "docs:architecture",
                "# Architecture Overview\n\nAPI is the only Cosmos writer. Domain is pure C#.")
        };

        foreach (var (path, context, content) in docs)
        {
            var contentWithMeta = $"[{context}] {path}\n\n{content}";
            var request = new MemoryWriteRequest("jsaa", contentWithMeta, AgentId: path);
            var entry = await _store.WriteAsync(request, TestContext.Current.CancellationToken);
            chunkMap[path] = ComputeExpectedHash(contentWithMeta);
        }

        await _store.ConfigureEmbeddingAsync("jsaa", "local", null, null, null,
            TestContext.Current.CancellationToken);
        await _store.EmbedPendingAsync("jsaa", null, TestContext.Current.CancellationToken);
        return chunkMap;
    }

    private static List<(string RelPath, string FileType)> EnumerateFiles(string root)
    {
        var result = new List<(string, string)>();
        foreach (var glob in IncludeGlobs)
        {
            var dir = Path.GetDirectoryName(glob) ?? ".";
            var pattern = Path.GetFileName(glob);
            var searchDir = Path.Combine(root, dir);
            if (!Directory.Exists(searchDir))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(searchDir, pattern, SearchOption.TopDirectoryOnly))
            {
                var relPath = Path.GetRelativePath(root, file);
                if (ExcludeGlobs.Any(ex => MatchesGlob(relPath, ex)))
                {
                    continue;
                }

                result.Add((relPath, ClassifyFile(relPath)));
            }
        }

        return result;
    }

    private static bool MatchesGlob(string path, string glob)
    {
        if (glob.EndsWith("/**"))
        {
            return path.StartsWith(glob[..^3]);
        }

        if (glob.Contains('*'))
        {
            return path.StartsWith(glob[..glob.IndexOf('*')]);
        }

        return path == glob;
    }

    private static string ClassifyFile(string relPath)
    {
        if (relPath.StartsWith("docs/adr/"))
        {
            return "adr";
        }

        if (relPath is "docs/architecture.md" or "docs/data-model.md"
            or "docs/flows.md" or "docs/requirements.md" or "docs/CHANGELOG.md")
        {
            return "architecture";
        }

        if (relPath.StartsWith("docs/explanation/"))
        {
            return "explanation";
        }

        if (relPath.StartsWith("docs/how-to/"))
        {
            return "howto";
        }

        if (relPath.StartsWith("docs/reference/"))
        {
            return "reference";
        }

        if (relPath.StartsWith("docs/rules/"))
        {
            return "rules";
        }

        if (relPath.StartsWith(".ai-badger/invariants/"))
        {
            return "invariants";
        }

        if (relPath.Contains("/skills/") && relPath.EndsWith("SKILL.md"))
        {
            return "skills";
        }

        if (relPath.StartsWith(".ai-badger/agents/"))
        {
            return "agents";
        }

        if (relPath.StartsWith(".ai-badger/instructions/"))
        {
            return "instructions";
        }

        if (relPath.StartsWith(".ai-badger/") && relPath.EndsWith(".json"))
        {
            return "config";
        }

        if (relPath.StartsWith(".ai-badger/") && relPath.EndsWith(".md"))
        {
            return "config";
        }

        if (relPath.StartsWith(".ai-badger/agent-instructions/"))
        {
            return "agent-model";
        }

        if (relPath.StartsWith(".remember/"))
        {
            return "remember";
        }

        if (relPath.StartsWith("docs/tutorials/"))
        {
            return "tutorials";
        }

        if (relPath.StartsWith("docs/legacy/"))
        {
            return "legacy";
        }

        if (relPath.StartsWith("docs/meta/"))
        {
            return "meta";
        }

        if (relPath is "README.md" or "CLAUDE.md" or "HERMES.md" or "REVIEW.md")
        {
            return "root-md";
        }

        if (relPath.StartsWith("infra/"))
        {
            return "infra";
        }

        return "unknown";
    }

    private static string SourcePrefix(string relPath, string fileType)
    {
        if (relPath.StartsWith("docs/"))
        {
            return fileType switch
            {
                "adr" => "docs:adr",
                "explanation" => "docs:explanation",
                "howto" => "docs:how-to",
                "reference" => "docs:reference",
                "rules" => "docs:rules",
                "tutorials" => "docs:tutorials",
                "legacy" => "docs:legacy",
                "meta" => "docs:meta",
                _ => "docs:architecture"
            };
        }

        if (relPath.StartsWith(".ai-badger/"))
        {
            return "ai-badger";
        }

        if (relPath.StartsWith(".remember/"))
        {
            return "remember";
        }

        return "docs:architecture";
    }

    private static string ShortRel(string relPath)
    {
        if (relPath.StartsWith("docs/"))
        {
            var parts = relPath.Split('/', 3);
            if (parts.Length >= 3)
            {
                return parts[2];
            }

            if (parts.Length == 2)
            {
                return parts[1];
            }

            return relPath;
        }

        if (relPath.StartsWith(".ai-badger/"))
        {
            return relPath[".ai-badger/".Length..];
        }

        if (relPath.StartsWith(".remember/"))
        {
            return relPath[".remember/".Length..];
        }

        return relPath;
    }

    private static string ChunkPath(string relPath, string fileType, string section)
    {
        var prefix = SourcePrefix(relPath, fileType);
        var shortPath = ShortRel(relPath);
        if (string.IsNullOrEmpty(section))
        {
            return $"{prefix}:{shortPath}";
        }

        return $"{prefix}:{shortPath}#{section}";
    }

    private static string ContextLabel(string relPath, string fileType) =>
        fileType switch
        {
            "adr" => "docs:adr",
            "architecture" => "docs:architecture",
            "explanation" => "docs:explanation",
            "howto" => "docs:how-to",
            "reference" => "docs:reference",
            "rules" => "docs:rules",
            "tutorials" => "docs:tutorials",
            "legacy" => "docs:legacy",
            "meta" => "docs:meta",
            "invariants" => "ai-badger:invariants",
            "skills" => "ai-badger:skills",
            "agents" => "ai-badger:agents",
            "instructions" => "ai-badger:instructions",
            "config" => "ai-badger:instructions",
            "agent-model" => "ai-badger:instructions",
            "remember" => "remember:operational",
            "root-md" => "docs:architecture",
            "infra" => "docs:architecture",
            _ => "docs:architecture"
        };

    private List<(string StructuredPath, string Context, string Content)> ChunkFile(
        string relPath, string fileType, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        return fileType switch
        {
            "adr" => ChunkAdr(relPath, fileType, content),
            "architecture" or "reference" or "root-md" or "explanation"
                or "howto" or "tutorials" or "legacy" or "infra"
                => ChunkByHeadings(relPath, fileType, content),
            _ =>
            [
                (ChunkPath(relPath, fileType, ""), ContextLabel(relPath, fileType), content)
            ]
        };
    }

    private List<(string, string, string)> ChunkAdr(string relPath, string fileType, string content)
    {
        var chunks = new List<(string, string, string)>();
        var ctx = ContextLabel(relPath, fileType);

        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentSection = "header";
        var currentBody = new StringBuilder();

        foreach (var line in content.Split('\n'))
        {
            if (line.StartsWith("## ") && line.Length > 3)
            {
                if (currentBody.Length > 0)
                {
                    sections[currentSection] = currentBody.ToString().Trim();
                }

                currentSection = line[3..].Trim().ToLowerInvariant();
                currentBody.Clear();
            }

            currentBody.AppendLine(line);
        }

        if (currentBody.Length > 0)
        {
            sections[currentSection] = currentBody.ToString().Trim();
        }

        if (content.Length < 1000)
        {
            chunks.Add((ChunkPath(relPath, fileType, ""), ctx, content));
            return chunks;
        }

        if (sections.TryGetValue("header", out var header))
        {
            chunks.Add((ChunkPath(relPath, fileType, "header"), ctx, header));
        }

        foreach (var (section, body) in sections)
        {
            if (section == "header")
            {
                continue;
            }

            chunks.Add((ChunkPath(relPath, fileType, section), ctx, body));
        }

        if (chunks.Count == 0)
        {
            chunks.Add((ChunkPath(relPath, fileType, ""), ctx, content));
        }

        return chunks;
    }

    private List<(string, string, string)> ChunkByHeadings(string relPath, string fileType,
        string content)
    {
        var chunks = new List<(string, string, string)>();
        var ctx = ContextLabel(relPath, fileType);
        var h1Title = "";

        var sections = new List<(string Heading, string Body)>();
        var currentHeading = "";
        var currentBody = new StringBuilder();

        foreach (var line in content.Split('\n'))
        {
            if (line.StartsWith("# ") && h1Title.Length == 0)
            {
                h1Title = line;
                currentBody.AppendLine(line);
            }
            else if (line.StartsWith("## "))
            {
                if (currentBody.Length > 0)
                {
                    sections.Add((currentHeading, currentBody.ToString().Trim()));
                }

                currentHeading = line[3..].Trim();
                currentBody.Clear();
                currentBody.AppendLine(h1Title);
                currentBody.AppendLine(line);
            }
            else
            {
                currentBody.AppendLine(line);
            }
        }

        if (currentBody.Length > 0)
        {
            sections.Add((currentHeading, currentBody.ToString().Trim()));
        }

        if (sections.Count == 0)
        {
            chunks.Add((ChunkPath(relPath, fileType, ""), ctx, content));
            return chunks;
        }

        foreach (var (heading, body) in sections)
        {
            var section = string.IsNullOrEmpty(heading) ? "" : SanitizeSection(heading);
            chunks.Add((ChunkPath(relPath, fileType, section), ctx, body));
        }

        return chunks;
    }

    private static string SanitizeSection(string heading) => heading.Replace(' ', '-').Replace("/", "-").ToLowerInvariant();

    private static string ComputeExpectedHash(string content)
    {
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var assignedPath = contentHash + ".md";
        var combined = Encoding.UTF8.GetBytes(assignedPath)
            .Concat(Encoding.UTF8.GetBytes(content)).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(combined));
    }

    private static BaselineQuery[] LoadQueries()
    {
        var projectRoot = FindProjectRoot();
        var queriesPath = Path.Combine(projectRoot, "scripts", "baseline-queries.json");
        if (!File.Exists(queriesPath))
        {
            return
            [
                new BaselineQuery("C1", "Invariants & Conventions", "Is TDD required?",
                    "ai-badger:invariants/tdd-mandatory.md",
                    "Yes — Write a failing, behavior-focused test before any production code change",
                    "project", 5, false)
            ];
        }

        var json = File.ReadAllText(queriesPath);
        return JsonSerializer.Deserialize<BaselineQuery[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx"))
                || File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string CreateTempRoot() =>
        TestData.CreateTempRoot("ai-raccoon-tests");

    public sealed record BaselineQuery(
        string Id,
        string Category,
        string Query,
        string? ExpectedSource,
        string? ExpectedKnowledge,
        string Scope,
        int SearchLimit,
        bool NegativeTest);

    public sealed record QueryResult(
        string Id,
        string Category,
        string Query,
        string? ExpectedSource,
        string? ExpectedKnowledge,
        IReadOnlyList<ResultEntry> Results);

    public sealed record ResultEntry(
        int Rank,
        string Hash,
        double Ranking,
        string Path,
        string Snippet,
        bool IsExpectedSource);

    public sealed record BaselineReport(
        string ProjectId,
        DateTimeOffset ExportedAt,
        int QueryCount,
        int QueriesWithResults,
        int ExpectedSourceMatchesAtTop3,
        IReadOnlyList<QueryResult> QueryResults);
}
