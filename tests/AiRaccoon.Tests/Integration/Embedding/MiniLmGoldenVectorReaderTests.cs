using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     C6: the eval-set-100 capture path must read both committed corpus shapes — the
///     pre-header bare array and the header-shaped {header, queries} root it has today.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MiniLmGoldenVectorReaderTests
{
    private const string BareArrayJson = """
        [
          {"id": "E001", "query": "alpha"},
          {"id": "E002", "query": "beta"}
        ]
        """;

    private const string HeaderShapeJson = """
        {
          "header": {"generator": "build_eval_corpus.py", "seed": 42, "queryCount": 2,
                     "snapshotSha256": "e0434a7214ac4caf1dbbef56147f582515bd0f5ebda665ad8cb06687296a55f6"},
          "queries": [
            {"id": "E001", "query": "alpha"},
            {"id": "E002", "query": "beta"}
          ]
        }
        """;

    [RetryFact]
    public void ReadEvalSetQueries_ReadsTheBareArrayShape()
    {
        var queries = MiniLmGoldenVectorReader.ReadEvalSetQueries(BareArrayJson);
        queries.Count.ShouldBe(2);
        queries[0].ShouldBe(("E001", "alpha"));
        queries[1].ShouldBe(("E002", "beta"));
    }

    [RetryFact]
    public void ReadEvalSetQueries_ReadsTheHeaderShape()
    {
        var queries = MiniLmGoldenVectorReader.ReadEvalSetQueries(HeaderShapeJson);
        queries.Count.ShouldBe(2);
        queries[0].ShouldBe(("E001", "alpha"));
        queries[1].ShouldBe(("E002", "beta"));
    }

    [RetryFact]
    public void ReadEvalSetQueries_DoesNotConfuseTheHeaderWithAQuery()
    {
        var queries = MiniLmGoldenVectorReader.ReadEvalSetQueries(HeaderShapeJson);
        queries.ShouldNotContain(q => q.Id == "generator");
        queries.Select(q => q.Id).ShouldBe(["E001", "E002"]);
    }
}
