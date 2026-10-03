using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>Resolved chunk sizing for one ingest: unlike <see cref="ChunkBudget" />, the counter is optional — null when the configured provider is not "local", so the chunker falls back to its own default counter.</summary>
public sealed record ChunkSize(int MaxTokens, int OverlayTokens, TokenCount? CountTokens);

/// <summary>The one chunk-budget resolution shared by file ingest and the repair family, so a repaired chunk is cut exactly as a fresh ingest would cut it (D9).</summary>
public static class ChunkSizeReader
{
    /// <summary>The engine an unconfigured bank will embed with once one is configured (docs/adr/0063).</summary>
    private const string BundledProvider = "local";

    extension(SqliteConnection connection)
    {
        /// <summary>
        ///     Resolves the chunk budget from the engine that will embed these chunks (docs/adr/0036):
        ///     "local" also supplies the real engine tokenizer as the counting override — the manifest
        ///     tokenizer for manifest models, the bundled wordpiece tokenizer otherwise — so the budget
        ///     and the counter that enforces it always agree with what will actually embed the chunk
        ///     (D9). Other providers keep the default o200k counter.
        ///     <para>
        ///         An **unset** provider resolves to the bundled local engine rather than to the default
        ///         o200k budget (docs/adr/0063). Nothing embeds yet at that point, but the boundaries
        ///         drawn now are the ones the engine is handed later, and configuring an engine re-embeds
        ///         the bank without re-chunking it — so ingest-then-configure, a supported order, made
        ///         those boundaries permanently wrong. Chunking to the most restrictive plausible window
        ///         is safe in the other direction: a chunk that fits the bundled model fits a larger one.
        ///     </para>
        ///     <para>
        ///         Known, deliberately unaddressed gap: a long punctuation-free, newline-joined run (e.g. a
        ///         hash list) can collapse to a single [UNK] under a pretokenizer, reporting an
        ///         implausibly small count that a budget ceiling alone would not catch.
        ///         <see cref="OnnxEmbeddingGenerator" />'s embed-time detector makes it visible.
        ///     </para>
        /// </summary>
        public async Task<ChunkSize> ReadChunkSizeAsync(IEmbeddingService embeddingService,
            CancellationToken cancellationToken)
        {
            var configured = await connection.ReadSettingAsync(EmbeddingSettingsKeys.Provider, cancellationToken);
            var provider = string.IsNullOrWhiteSpace(configured) ? BundledProvider : configured;

            var model = await connection.ReadSettingAsync(EmbeddingSettingsKeys.Model, cancellationToken);
            var settings = new EmbeddingSettings(provider, model, null, null);
            var maxTokens = embeddingService.ResolveChunkBudgetFor(settings);
            var overlayTokens = Math.Min(ChunkingDefaults.OverlayTokens, Math.Max(0, maxTokens - 1));
            var countTokens = provider.Equals(BundledProvider, StringComparison.OrdinalIgnoreCase)
                ? new TokenCount(embeddingService.ResolveTokenizer(settings)!.CountTokens)
                : null;
            return new ChunkSize(maxTokens, overlayTokens, countTokens);
        }

        /// <summary>The repair family's view of <see cref="ReadChunkSizeAsync" />: the same sizing, with the o200k proxy named explicitly where ingest leaves the chunker's default counter.</summary>
        public async Task<ChunkBudget> ReadChunkBudgetAsync(IEmbeddingService embeddingService,
            CancellationToken cancellationToken)
        {
            var size = await connection.ReadChunkSizeAsync(embeddingService, cancellationToken);
            return new ChunkBudget(size.MaxTokens, size.OverlayTokens,
                size.CountTokens ?? new TokenCount(new O200kTokenizer().CountTokens));
        }
    }
}
