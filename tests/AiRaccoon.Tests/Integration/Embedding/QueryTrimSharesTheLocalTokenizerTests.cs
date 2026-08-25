using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Before this change, <see cref="EmbeddingService.TrimQueryToWindow" /> rebuilt the bundled BERT
///     WordPiece tokenizer (231 KB vocab.txt) from scratch on every search query. It now takes the
///     shared <see cref="LocalTokenizer" /> as a dependency and must reuse it across calls.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class QueryTrimSharesTheLocalTokenizerTests
{
    [RetryFact]
    public void TrimQueryToWindow_CalledRepeatedly_BuildsTheLocalTokenizerOnce()
    {
        var builds = 0;
        var localTokenizer = new LocalTokenizer(() =>
        {
            builds++;
            return OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        });
        var service = new EmbeddingService(new FakeLogger<EmbeddingService>(), localTokenizer, new EmbeddingTokenizerFactory(), new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()), NoOpMeasurementRecorder.Instance, TimeProvider.System);
        var settings = new EmbeddingSettings("local", null, null, null);

        service.TrimQueryToWindow(settings, LongQuery());
        service.TrimQueryToWindow(settings, LongQuery());
        service.TrimQueryToWindow(settings, "a short query");

        builds.ShouldBe(1, "TrimQueryToWindow must reuse the shared tokenizer, not rebuild it per query");
    }

    /// <summary>The other half of the laziness contract: a non-local provider returns early
    /// (EmbeddingService.cs's own guard) and must never touch the tokenizer at all.</summary>
    [RetryFact]
    public void TrimQueryToWindow_NonLocalProvider_NeverBuildsTheLocalTokenizer()
    {
        var builds = 0;
        var localTokenizer = new LocalTokenizer(() =>
        {
            builds++;
            return OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        });
        var service = new EmbeddingService(new FakeLogger<EmbeddingService>(), localTokenizer, new EmbeddingTokenizerFactory(), new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()), NoOpMeasurementRecorder.Instance, TimeProvider.System);
        var settings = new EmbeddingSettings("openai", "text-embedding-3-small", null, null);

        service.TrimQueryToWindow(settings, LongQuery());

        builds.ShouldBe(0, "a non-local provider must never touch the local BERT tokenizer");
    }

    private static string LongQuery() =>
        string.Join(' ', Enumerable.Repeat(
            "how does the retrieval pipeline weigh full text against vectors when the corpus is large", 40));
}
