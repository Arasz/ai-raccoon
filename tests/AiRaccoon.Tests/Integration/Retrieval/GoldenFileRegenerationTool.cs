using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Tests.Unit.Retrieval;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     Regenerates tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json by running the
///     vendored ReferenceRunner (pinned sqlite-memory 1.3.5 + the pinned MiniLM gguf) over the
///     current RealWorldCorpus/RealWorldQueries (ai-raccoon#455, ADR-0090 precedent). Env-gated
///     because it overwrites a committed golden every parity-gate assertion measures against —
///     mirrors DocsCorpusRegenerationTool's gate for docs-memory.db.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class GoldenFileRegenerationTool(ITestOutputHelper output)
{
    private const string RunEnvVar = "AIRACCOON_REGENERATE_PARITY_GOLDEN";

    [Fact]
    public async Task RegenerateReferenceTopK()
    {
        if (Environment.GetEnvironmentVariable(RunEnvVar) != "1")
        {
            Assert.Skip($"{RunEnvVar} not set — this tool overwrites the committed " +
                        $"{GoldenFile.FileName} golden that ParityGateTests measures against. " +
                        $"Set {RunEnvVar}=1 to run it.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var ensured = await ReferenceAssets.EnsureAsync(cancellationToken);
        ensured.AllPresent.ShouldBeTrue($"Reference assets missing: {string.Join("; ", ensured.Errors)}");

        var run = await ReferenceRunner.RunAsync(
            RealWorldCorpus.Documents, RealWorldQueries.Queries, cancellationToken: cancellationToken);

        var path = Path.Combine(ReferenceAssets.AssetsDirectory, GoldenFile.FileName);
        var golden = GoldenFile.FromRun(run) with { Path = path };
        golden.Save();

        output.WriteLine($"wrote {path}: {run.DocumentCount} docs, {run.ResultsByQuery.Count} queries, " +
                         $"engine {run.Engine}, model {run.Model}");
    }
}
