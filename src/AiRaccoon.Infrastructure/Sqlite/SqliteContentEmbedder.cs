using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     IContentEmbedder over IEntryEmbedder.EmbedQueryAsync (ADR-0039): reuses the bank's already-
///     configured embedding engine instead of re-implementing engine resolution. Not currently
///     called from the write path — see docs/adr/0039 for why.
/// </summary>
public sealed class SqliteContentEmbedder(ISqliteConnectionFactory factory, IEntryEmbedder entryEmbedder) : IContentEmbedder
{
    public async Task<float[]?> EmbedContentAsync(string content, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await entryEmbedder.EmbedQueryAsync(connection, content, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : EmbeddingBlob.ToFloats(bytes);
    }
}
