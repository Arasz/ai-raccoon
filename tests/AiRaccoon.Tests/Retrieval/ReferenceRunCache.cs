using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Tests.Retrieval.Assets;
using AiRaccoon.Tests.Retrieval.Reference;

namespace AiRaccoon.Tests.Retrieval;

/// <summary>
///     One full reference run shared by every harness test in the process: embedding the corpus
///     is the expensive step, and the golden gate + smoke test both read from the same run.
/// </summary>
public static class ReferenceRunCache
{
    private static readonly Lazy<Task<ReferenceRun>> Run = new(BuildAsync);

    public static Task<ReferenceRun> GetAsync() => Run.Value;

    private static async Task<ReferenceRun> BuildAsync()
    {
        var ensured = await ReferenceAssets.EnsureAsync().ConfigureAwait(false);
        if (!ensured.AllPresent)
        {
            throw new InvalidOperationException(
                $"Reference assets missing: {string.Join("; ", ensured.Errors)}");
        }

        return await ReferenceRunner.RunAsync(
            RealWorldCorpus.Documents, RealWorldQueries.Queries).ConfigureAwait(false);
    }
}
