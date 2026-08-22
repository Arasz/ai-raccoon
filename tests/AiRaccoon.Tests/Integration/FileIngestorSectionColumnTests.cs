using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     SourcePathQuery resolves a "file#section" query by ANDing the anchor against the FTS
///     columns {source_file section}. FileIngestor wrote section as null for every chunk, so that
///     documented behaviour could not work for any ingested document — the bulk of every bank.
///     Measured on the live bank before the fix: section populated on 517 of 15,325 rows (all from
///     memory_write's explicit section argument) against heading_path on 5,604.
///     The old retrieval fixture hid this because it was built by scripts/src/chunking.py, a
///     pipeline that does populate section and that the product does not ship.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public class FileIngestorSectionColumnTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly FileIngestor _ingestor;
    private readonly IModelMigrationLease _modelMigrationLease = Substitute.For<IModelMigrationLease>();
    private readonly string _testDir;
    private readonly TimeProvider _timeProvider = new FakeTimeProvider();

    public FileIngestorSectionColumnTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "airaccoon_section_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var opts = new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _conn = factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();

        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        _ingestor = new FileIngestor(matcher, new EntryEmbedder(TestData.CreateEmbeddingService(), _modelMigrationLease, _timeProvider),
            new SqliteMemorySourceStore(factory), TimeProvider.System, TestData.CreateEmbeddingService());

        using var scopeCmd = _conn.CreateCommand();
        scopeCmd.CommandText = "INSERT INTO settings (key, value) VALUES (@key, @scope);";
        scopeCmd.Parameters.AddWithValue("@key", IngestScopeKeys.ScopeGlobal);
        scopeCmd.Parameters.AddWithValue("@scope", IngestScopeKeys.Serialize([_testDir]));
        scopeCmd.ExecuteNonQuery();
    }

    /// <summary>heading_path is only written by <see cref="EntryEmbedder"/>'s inline embed, which is
    /// a no-op with no engine configured (<see cref="EntryEmbedder.EmbedIfConfiguredAsync"/>).</summary>
    private void ConfigureLocalEmbeddingProvider()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES (@key, @provider);";
        cmd.Parameters.AddWithValue("@key", EmbeddingSettingsKeys.Provider);
        cmd.Parameters.AddWithValue("@provider", "local");
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _conn.Dispose();
        TestData.DeleteTempRoot(_testDir);
    }

    [Fact]
    public async Task IngestFileAsync_PopulatesSectionFromTheHeadingLeaf()
    {
        var file = Path.Combine(_testDir, "adr.md");
        await File.WriteAllTextAsync(file,
            "# ADR-0011 Frontend chassis\n\n## Context\n\nThe app needs a shell.\n\n## Decision\n\nWe adopt the chassis pattern.\n",
            TestContext.Current.CancellationToken);

        await _ingestor.IngestFileAsync(_conn, "acme", file, null, TestContext.Current.CancellationToken);

        var sections = new List<string?>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT section FROM entries WHERE source_file = @p ORDER BY chunk_index";
        cmd.Parameters.AddWithValue("@p", file);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            sections.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        sections.ShouldNotBeEmpty();
        sections.Where(s => s != null).ShouldContain(s => s!.Contains("Decision", StringComparison.OrdinalIgnoreCase),
            "the chunk under '## Decision' must carry that heading as its section, or a '#decision' " +
            "path query has nothing to match against");
    }

    /// <summary>
    ///     docs/adr/0048: an oversized code fence used to be split at line granularity, so a chunk
    ///     could begin inside the block and the block's '#' shell comments became the section — a
    ///     sentence of prose in an FTS-weighted column. Measured on the committed jsaa corpus:
    ///     3 of 871 headed rows, worst case a 99-character README section.
    /// </summary>
    [Fact]
    public async Task IngestFileAsync_OversizedShellFence_NeverTakesTheSectionFromInsideTheFence()
    {
        var file = Path.Combine(_testDir, "readme.md");
        var body = string.Join("\n", Enumerable.Range(1, 40).Select(i =>
            $"# shell comment {i} explaining the credential path that the next command exercises\ncurl -i http://localhost:7071/api/operations/{i}"));
        await File.WriteAllTextAsync(file,
            $"# ADR-0060 Authentication arms\n\n## Local development\n\n```bash\n{body}\n```\n\nTrailing prose.\n",
            TestContext.Current.CancellationToken);

        await _ingestor.IngestFileAsync(_conn, "acme", file, null, TestContext.Current.CancellationToken);

        var sections = new List<string?>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT section FROM entries WHERE source_file = @p ORDER BY chunk_index";
        cmd.Parameters.AddWithValue("@p", file);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            sections.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        sections.ShouldNotBeEmpty();
        sections.ShouldNotContain(s => s != null && s.Contains("shell comment", StringComparison.Ordinal),
            "a '#' comment inside a code fence is not a heading — it must never reach the section column");
    }

    /// <summary>
    ///     HeadingPathParser joins segments with " &gt; ", so the leaf cannot be taken by splitting on
    ///     the bare '&gt;' character: a heading carrying one of its own (an HTML comment, a
    ///     placeholder) then ends the split on an empty segment and the section goes back to null.
    ///     Measured on the regenerated corpus: 5 of 871 headed rows.
    /// </summary>
    /// <summary>
    ///     Issue #489: the greedy pack let '## Residual offset' squeeze into the tail of chunk 0
    ///     even though none of its own content fit alongside it, so chunk 0 (all 'Leveled bench'
    ///     content) got mislabeled with the next section's heading. Sizes mirror the checklist
    ///     fixture (docs/work/checklist/2026-08-22-1.32.0-check.json) that first surfaced it.
    /// </summary>
    [Fact]
    public async Task IngestFileAsync_HeadingWouldDangleAtChunkTail_LabelsChunksWithTheirOwnSection()
    {
        var file = Path.Combine(_testDir, "quadrant.md");
        var levelledBench =
            "Before any reading is trusted, the quadrant is set on a leveled bench and its bubble is checked " +
            "against the reference mark until it stops drifting. The technician records the ambient temperature, " +
            "the humidity, and the exact orientation of the bench relative to true north, because thermal " +
            "expansion of the brass arm can itself introduce a reading error that looks identical to a genuine " +
            "offset. A log entry is made for every session: date, operator initials, bench serial number, and " +
            "the three consecutive bubble readings taken a minute apart. Only when all three agree within the " +
            "stated tolerance does the technician proceed to the next stage of the calibration procedure, and " +
            "any disagreement sends the bench back through the leveling screws before a fourth attempt. This " +
            "whole procedure exists because a quadrant that starts a session unleveled will silently carry that " +
            "tilt into every subsequent star sight for the rest of the watch, and nobody discovers it until the " +
            "navigator's noon-sight fix disagrees wildly with dead reckoning. The leveling ritual is therefore " +
            "treated as a hard prerequisite, never a courtesy step, and skipping it voids the day's readings entirely.";
        var residualOffset =
            "A quadrant that always reads a little too high or too low by the same fixed amount has a residual " +
            "offset, and once that steady error is discovered and logged, it is subtracted automatically from " +
            "every later reading so the same defect is never diagnosed twice. The offset is found by comparing " +
            "the instrument against a known star at a well-documented altitude, on a night when refraction " +
            "tables are considered reliable, and the difference between the observed and the almanac value " +
            "becomes the correction constant. That constant is engraved on a small brass tag riveted to the " +
            "pivot screw housing, dated and initialed by whoever ran the comparison, and every subsequent user " +
            "checks the tag before trusting a sight. A constant left unchecked for too long drifts as the " +
            "instrument ages, so the calibration is repeated on a fixed schedule rather than only when a " +
            "reading looks suspicious, because a suspicious reading is often the first symptom of a problem " +
            "that has been quietly growing for months.";
        await File.WriteAllTextAsync(file,
            $"# Quadrant calibration\n\n## Leveled bench\n\n{levelledBench}\n\n## Residual offset\n\n{residualOffset}\n",
            TestContext.Current.CancellationToken);
        ConfigureLocalEmbeddingProvider();

        await _ingestor.IngestFileAsync(_conn, "acme", file, null, TestContext.Current.CancellationToken);

        var rows = new List<(string? Section, string? HeadingPath, string Value)>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT section, heading_path, value FROM entries WHERE source_file = @p ORDER BY chunk_index";
        cmd.Parameters.AddWithValue("@p", file);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2)));
        }

        rows.Count.ShouldBeGreaterThanOrEqualTo(2, "the fixture is sized to split across at least two chunks");
        rows[0].Value.ShouldNotContain("## Residual offset");
        rows[0].Section.ShouldBe("Leveled bench");
        rows[0].HeadingPath.ShouldBe("Quadrant calibration > Leveled bench");
        rows.Skip(1).ShouldContain(row => row.Section == "Residual offset");
    }

    [Fact]
    public async Task IngestFileAsync_HeadingContainingAngleBracket_StillPopulatesSection()
    {
        var file = Path.Combine(_testDir, "template.md");
        await File.WriteAllTextAsync(file,
            "# Differential template\n\n## 1. Current state <!-- REQUIRED -->\n\nWhat exists today.\n",
            TestContext.Current.CancellationToken);

        await _ingestor.IngestFileAsync(_conn, "acme", file, null, TestContext.Current.CancellationToken);

        var sections = new List<string?>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT section FROM entries WHERE source_file = @p ORDER BY chunk_index";
        cmd.Parameters.AddWithValue("@p", file);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            sections.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        sections.ShouldNotBeEmpty();
        sections.ShouldContain(s => s != null && s.Contains("Current state", StringComparison.Ordinal),
            "the leaf must survive a heading that carries a '>' of its own");
    }
}
