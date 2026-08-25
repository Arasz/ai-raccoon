using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     Guards the property ADR-0077 found missing everywhere else: this corpus can see tables. The
///     retrieval gate that scores it is only worth running while every graded answer is still a
///     table cell, so these checks are cheap and run on the fast lane.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class TableCorpusIntegrityTests
{
    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [RetryFact]
    public void EveryCorpusDocument_ContainsAMarkdownTable()
    {
        var files = TableCorpusCatalog.CorpusFiles();

        files.Count.ShouldBeGreaterThanOrEqualTo(10, "a handful of documents is not a corpus");
        var tableless = files.Where(file => TableCorpusCatalog.TableLines(File.ReadAllText(file)).Count == 0).ToList();
        tableless.ShouldBeEmpty("every document in the table corpus must carry a markdown table");
    }

    [RetryFact]
    public void EveryQuery_PointsAtADocumentInTheCorpus()
    {
        var root = TableCorpusCatalog.CorpusRoot();

        var missing = TableCorpusCatalog.Load()
            .Where(query => !File.Exists(Path.Combine(root, query.ExpectedSource)))
            .Select(query => $"{query.Id} -> {query.ExpectedSource}")
            .ToList();

        missing.ShouldBeEmpty("a graded query whose expected document is absent can never be answered");
    }

    /// <summary>
    ///     The anchor must identify one place in the document. Two occurrences make the relevance set
    ///     depend on which one a chunk boundary happens to keep, which is the instability this
    ///     response variable exists to remove.
    /// </summary>
    [RetryFact]
    public void EveryAnswerSpan_OccursExactlyOnceInItsDocument()
    {
        var root = TableCorpusCatalog.CorpusRoot();
        var wrong = new List<string>();

        foreach (var query in TableCorpusCatalog.Load())
        {
            var text = Collapse(File.ReadAllText(Path.Combine(root, query.ExpectedSource)));
            var span = Collapse(query.AnswerSpan);
            var occurrences = 0;
            for (var at = text.IndexOf(span, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(span, at + span.Length, StringComparison.Ordinal))
            {
                occurrences++;
            }

            if (occurrences != 1)
            {
                wrong.Add($"{query.Id}: {occurrences} occurrences of '{query.AnswerSpan}'");
            }
        }

        wrong.ShouldBeEmpty();
    }

    /// <summary>
    ///     The check that keeps this a table measurement. A graded answer that drifts into prose still
    ///     scores, and the gate would go on reporting a number about tables it no longer measures —
    ///     ADR-0077's blindness, one layer up.
    /// </summary>
    [RetryFact]
    public void EveryAnswerSpan_LivesInsideATableRow()
    {
        var root = TableCorpusCatalog.CorpusRoot();
        var outsideTables = new List<string>();

        foreach (var query in TableCorpusCatalog.Load())
        {
            var tableText = TableCorpusCatalog.TableLines(File.ReadAllText(Path.Combine(root, query.ExpectedSource)))
                .Select(Collapse);
            if (!tableText.Any(line => line.Contains(Collapse(query.AnswerSpan), StringComparison.Ordinal)))
            {
                outsideTables.Add($"{query.Id}: '{query.AnswerSpan}' is not in a table row of {query.ExpectedSource}");
            }
        }

        outsideTables.ShouldBeEmpty();
    }

    /// <summary>A query that quotes its own answer measures string matching, not retrieval.</summary>
    [RetryFact]
    public void NoQuery_QuotesItsOwnAnswerSpan()
    {
        var leaking = TableCorpusCatalog.Load()
            .Where(query => Collapse(query.Query).Contains(Collapse(query.AnswerSpan), StringComparison.OrdinalIgnoreCase))
            .Select(query => query.Id)
            .ToList();

        leaking.ShouldBeEmpty();
    }

    /// <summary>
    ///     The manifest and the vendored directory are two lists of the same thing, so nothing but a
    ///     comparison keeps them together: a file added to one side and not the other leaves either an
    ///     ungraded document in the corpus or a pin describing something that is not there.
    /// </summary>
    [RetryFact]
    public void TheVendoredCorpus_IsExactlyWhatTheManifestDeclares()
    {
        var root = TableCorpusCatalog.CorpusRoot();
        var onDisk = TableCorpusCatalog.CorpusFiles()
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        onDisk.ShouldBe(TableCorpusCatalog.DeclaredFiles(),
            "scripts/table-corpus-sources.json and tests/AiRaccoon.Tests/Resources/TableCorpus disagree; " +
            "re-run scripts/vendor-table-corpus.py");
    }

    [RetryFact]
    public void QueryIds_AreUnique()
    {
        var queries = TableCorpusCatalog.Load();

        queries.Count.ShouldBeGreaterThanOrEqualTo(12);
        queries.Select(query => query.Id).Distinct(StringComparer.Ordinal).Count().ShouldBe(queries.Count);
    }
}
