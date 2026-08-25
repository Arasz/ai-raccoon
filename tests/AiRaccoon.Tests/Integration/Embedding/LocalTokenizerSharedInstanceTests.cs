using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     <see cref="LocalTokenizer" /> replaces per-call-site construction of the bundled BERT
///     WordPiece tokenizer with one shared, lazily-built instance (parsing the ~231 KB vocab.txt is
///     the expensive part). These tests build the real tokenizer against the real bundled vocab, so
///     they belong in the Integration/Slow tier alongside the other real-BertTokenizer tests
///     (QueryTruncationTests, ChunkBudgetIsEngineAwareTests).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class LocalTokenizerSharedInstanceTests
{
    private const string Text = "how does the retrieval pipeline weigh full text against vectors when the corpus is large";

    [RetryFact]
    public void CountTokens_CalledRepeatedly_BuildsTheUnderlyingTokenizerExactlyOnce()
    {
        var builds = 0;
        var tokenizer = new LocalTokenizer(() =>
        {
            builds++;
            return OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        });

        tokenizer.CountTokens("first query");
        tokenizer.CountTokens("second, different query");
        tokenizer.CountTokens("a third query, longer than the first two put together");

        builds.ShouldBe(1, "one real BertTokenizer must be built and reused, not rebuilt per call");
    }

    [RetryFact]
    public void CountTokens_UsesTheBundledVocab_AndCountsCorrectly()
    {
        var tokenizer = new LocalTokenizer();
        var reference = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());

        tokenizer.CountTokens(Text).ShouldBe(reference.CountTokens(Text));
    }

    /// <summary>
    ///     Many threads racing the first CountTokens call: Lazy&lt;T&gt;'s default
    ///     ExecutionAndPublication mode must still build exactly once, and every caller — whichever
    ///     lost the race — must see the one published instance's count, matching a serial call.
    /// </summary>
    [RetryFact]
    public void CountTokens_ManyConcurrentFirstCallers_BuildsExactlyOnce_AndEveryoneSeesTheSameCount()
    {
        var builds = 0;
        var tokenizer = new LocalTokenizer(() =>
        {
            Interlocked.Increment(ref builds);
            return OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        });
        var expected = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath()).CountTokens(Text);

        const int callers = 32;
        using var start = new Barrier(callers);
        var results = new int[callers];
        var threads = new Thread[callers];
        for (var i = 0; i < callers; i++)
        {
            var slot = i;
            threads[slot] = new Thread(() =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(10));
                results[slot] = tokenizer.CountTokens(Text);
            });
            threads[slot].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue("every caller thread must finish within the patience window");
        }

        builds.ShouldBe(1, "concurrent first use must still build the tokenizer exactly once");
        results.ShouldAllBe(count => count == expected);
    }
}
