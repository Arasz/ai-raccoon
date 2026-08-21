using System.Diagnostics.CodeAnalysis;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     The bank's file-ingestion mechanics: scope containment, chunking, and chunk insertion.
///     Takes an already-open connection rather than opening its own — every caller has already
///     opened the bank once for the whole walk, so the compiler enforces one-bank-open-per-ingest.
/// </summary>
public sealed class FileIngestor(
    IFileTypeMatcher fileTypeMatcher,
    IEntryEmbedder embedder,
    IMemorySourceStore sourceStore,
    TimeProvider timeProvider,
    ILocalTokenizer localTokenizer,
    IIgnoreRulesProvider? ignoreRulesProvider = null,
    ICodeFileTypeMatcher? codeFileTypeMatcher = null,
    ICodeIngestor? codeIngestor = null) : IFileIngestor
{
    private const int DefaultMaxTokens = 256;
    private readonly ICodeFileTypeMatcher _codeFileTypeMatcher = codeFileTypeMatcher ?? NullCodeFileTypeMatcher.Instance;
    private readonly ICodeIngestor _codeIngestor = codeIngestor ?? NullCodeIngestor.Instance;
    private readonly IIgnoreRulesProvider _ignoreRulesProvider = ignoreRulesProvider ?? NullIgnoreRulesProvider.Instance;

    /// <summary>
    ///     Set <paramref name="embedInline" /> false when the caller holds a write transaction: embedding
    ///     runs the engine per chunk, and a lock held that long stalls another process's first bank open.
    /// </summary>
    public async Task<int> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken, bool embedInline = true)
    {
        await RequireInScopeAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);

        if (IsHidden(path))
        {
            return 0;
        }

        if (!fileTypeMatcher.TryGetHandler(path, out var handler))
        {
            return await IngestAsCodeAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return await InsertChunksAsync(connection, projectId, path, content, handler, context, cancellationToken, embedInline)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     OQ4 (docs/work/2026-08-21-code-search-implementation-plan.md §12.4): explicit single-file
    ///     ingest routes code extensions to the code corpus too. No watch/ingest root is known here,
    ///     so the file's own parent directory is treated as the `ai-raccoon.ignore` root — the
    ///     closest analogue to `memory_ingest_directory` pointed at that directory.
    /// </summary>
    private async Task<int> IngestAsCodeAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken)
    {
        if (!_codeFileTypeMatcher.IsCodeFile(path))
        {
            return 0;
        }

        var root = Path.GetDirectoryName(path) ?? path;
        var ignoreRules = await _ignoreRulesProvider.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        if (IsIgnored(ignoreRules, root, path))
        {
            return 0;
        }

        return await _codeIngestor.IngestFileAsync(connection, projectId, path, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> IngestDirectoryAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        RequireInScope(scope, path);

        var ignoreRules = await _ignoreRulesProvider.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => !IsHidden(path, file) && !IsIgnored(ignoreRules, path, file) && IsInScope(scope, file))
            .OrderBy(file => file, StringComparer.Ordinal);

        var indexed = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fileTypeMatcher.TryGetHandler(file, out var handler))
            {
                if (_codeFileTypeMatcher.IsCodeFile(file))
                {
                    indexed += await _codeIngestor.IngestFileAsync(connection, projectId, file, cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            indexed += await InsertChunksAsync(connection, projectId, file, content, handler, context, cancellationToken)
                .ConfigureAwait(false);
        }

        return indexed;
    }

    /// <summary>
    ///     The same chunker and the same budget resolution file ingest uses, for content that never
    ///     came from a file (docs/adr/0064). Free-form notes are treated as markdown — that is what
    ///     agents write, and it is the handler a `.md` ingest would pick for the same text.
    /// </summary>
    public async Task<IReadOnlyList<string>> ChunkToBudgetAsync(SqliteConnection connection, string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var (maxTokens, overlayTokens, countTokens) = await ChunkSizeForAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        return IsIndexableFile("note.md", out var handler)
            ? handler.Chunker.Chunk(content, maxTokens, overlayTokens, countTokens)
            : [content];
    }

    /// <summary>Embeds one freshly inserted row when an engine is configured; deferred otherwise (FR-NM-3 s4; see docs/work/features-native-memory/native-memory.feature).</summary>
    private async Task<int> InsertChunksAsync(SqliteConnection connection, string projectId, string path,
        string content, IFileTypeHandler handler, string? context, CancellationToken cancellationToken, bool embedInline = true)
    {
        var resolvedContext = context ?? ContextNaming.ProjectContext(projectId);
        var bucket = EntryBucket.For(resolvedContext, projectId);
        var (chunkMaxTokens, chunkOverlayTokens, chunkCountTokens) = await ChunkSizeForAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var chunks = handler.Chunker.Chunk(content, chunkMaxTokens, chunkOverlayTokens, chunkCountTokens);
        if (chunks.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

        // Resolve source once for all chunks from this file.
        var source = await sourceStore.ResolveOrCreateOnConnectionAsync(
            connection, SourceType.File, path, null, null, cancellationToken).ConfigureAwait(false);

        // Document position is authoritative here (GH #371): the chunker just produced `chunks` in
        // document order, so every chunk's ordinal is written straight to chunk_index — for a
        // freshly inserted row and for one this pass merely rediscovers unchanged (dedup) alike.
        // Deriving it later from row-id order would put an edited-then-reinserted middle chunk last,
        // since dedup gives unchanged siblings the old (low) id and the edit the newest (highest) one.
        var inserted = 0;
        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            var chunk = chunks[ordinal];
            var hash = ContentHash.Of(path, chunk);
            // SourcePathQuery ANDs a "file#section" anchor against the FTS {source_file section}
            // columns, so a chunk with a null section can never satisfy one. Derived from the
            // chunk's own heading rather than left null; heading_path carries the full trail and
            // the anchor only ever names its leaf.
            var section = HeadingSection(chunk);
            var existingId = await connection.ExecuteScalarAsync<long?>(
                    Def(MemorySql.SelectChunkIdByPathAndHashInBucket,
                        new
                        {
                            hash,
                            path,
                            scope = bucket.Scope,
                            projectId = bucket.ProjectId,
                            contextLabel = bucket.ContextLabel,
                            workspaceId = bucket.WorkspaceId
                        }, cancellationToken))
                .ConfigureAwait(false);

            long chunkId;
            if (existingId is not null)
            {
                chunkId = existingId.Value;
            }
            else
            {
                await connection.ExecuteAsync(
                        Def(MemorySql.InsertEntry,
                            new
                            {
                                hash,
                                path,
                                value = chunk,
                                sourceFile = path,
                                section,
                                scope = bucket.Scope,
                                projectId = bucket.ProjectId,
                                contextLabel = bucket.ContextLabel,
                                workspaceId = bucket.WorkspaceId,
                                agentId = (string?)null,
                                createdAt = now,
                                updatedAt = now,
                                sourceId = source.Id,
                                chunkIndex = ordinal,
                                totalChunks = chunks.Count
                            },
                            cancellationToken))
                    .ConfigureAwait(false);

                var newId = await connection.ExecuteScalarAsync<long?>(
                        Def(MemorySql.SelectChunkIdByPathAndHashInBucket,
                            new
                            {
                                hash,
                                path,
                                scope = bucket.Scope,
                                projectId = bucket.ProjectId,
                                contextLabel = bucket.ContextLabel,
                                workspaceId = bucket.WorkspaceId
                            }, cancellationToken))
                    .ConfigureAwait(false);
                if (newId is null)
                {
                    continue;
                }

                chunkId = newId.Value;
                if (embedInline)
                {
                    await embedder.EmbedIfConfiguredAsync(connection, chunkId, chunk, cancellationToken)
                        .ConfigureAwait(false);
                }

                inserted++;
                continue;
            }

            await connection.ExecuteAsync(
                    Def(MemorySql.SetChunkPosition, new { id = chunkId, chunkIndex = ordinal, totalChunks = chunks.Count },
                        cancellationToken))
                .ConfigureAwait(false);
        }

        return inserted > 0 ? 1 : 0;
    }

    /// <summary>The engine an unconfigured bank will embed with once one is configured (docs/adr/0063).</summary>
    private const string BundledProvider = "local";

    /// <summary>
    ///     Resolves the chunk budget from the engine that will embed these chunks (docs/adr/0036):
    ///     "local" also supplies the real BERT tokenizer as the counting override, so the budget and
    ///     the counter that enforces it always agree with what will actually embed the chunk. Other
    ///     providers keep the default o200k counter — the mismatch this fixes is specific to the
    ///     bundled model.
    ///     <para>
    ///         An **unset** provider resolves to the bundled local engine rather than to the default
    ///         o200k budget (docs/adr/0063). Nothing embeds yet at that point, but the boundaries
    ///         drawn now are the ones the engine is handed later, and configuring an engine re-embeds
    ///         the bank without re-chunking it — so ingest-then-configure, a supported order, made
    ///         those boundaries permanently wrong. Chunking to the most restrictive plausible window
    ///         is safe in the other direction: a chunk that fits the bundled model fits a larger one.
    ///     </para>
    ///     <para>
    ///         "local" counts with the shared <see cref="LocalTokenizer" /> (docs/adr/0036) — the same
    ///         real BERT tokenizer that will actually embed the chunk, not the o200k budget proxy.
    ///         Known, deliberately unaddressed gap: a long punctuation-free, newline-joined run (e.g. a
    ///         hash list) can collapse to a single [UNK] under its pretokenizer, reporting an
    ///         implausibly small count that a budget ceiling alone would not catch. Measured against
    ///         the live bank at 1/15,246 entries affecting a 123-char fragment — real but low-impact;
    ///         <see cref="OnnxEmbeddingGenerator" />'s embed-time detector makes it visible.
    ///         Chunker-side remediation is out of scope for this wave.
    ///     </para>
    /// </summary>
    private async Task<ChunkSize> ChunkSizeForAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var configured = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Provider }, cancellationToken))
            .ConfigureAwait(false);
        var provider = string.IsNullOrWhiteSpace(configured) ? BundledProvider : configured;

        var model = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Model }, cancellationToken))
            .ConfigureAwait(false);
        var maxTokens = Math.Min(DefaultMaxTokens, EmbeddingService.SafeChunkBudgetFor(provider, model));
        var overlayTokens = Math.Min(ChunkingDefaults.OverlayTokens, Math.Max(0, maxTokens - 1));
        TokenCount? countTokens = provider.Equals("local", StringComparison.OrdinalIgnoreCase) ? localTokenizer.CountTokens : null;
        return new ChunkSize(maxTokens, overlayTokens, countTokens);
    }

    /// <summary>Resolved chunk sizing for one ingest: unlike <see cref="Ingestion.ChunkBudget" />, the
    /// counter is optional — null when the configured provider is not "local", so the chunker falls
    /// back to its own default counter.</summary>
    private sealed record ChunkSize(int MaxTokens, int OverlayTokens, TokenCount? CountTokens);

    private static async Task RequireInScopeAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        RequireInScope(scope, path);
    }

    private static async Task<IReadOnlyList<string>> ReadScopeAsync(SqliteConnection connection, string projectId,
        CancellationToken cancellationToken) =>
        IngestScopeKeys.Parse(
            await ReadSettingAsync(connection, IngestScopeKeys.ScopeProject(projectId), cancellationToken)
                .ConfigureAwait(false))
        ?? IngestScopeKeys.Parse(
            await ReadSettingAsync(connection, IngestScopeKeys.ScopeGlobal, cancellationToken)
                .ConfigureAwait(false))
        ?? [];

    private static void RequireInScope(IReadOnlyList<string> scope, string path)
    {
        var normalized = IngestPath.Normalize(path);
        if (!IsInScope(scope, normalized))
        {
            throw new PathOutsideScopeException(normalized);
        }
    }

    private static bool IsInScope(IReadOnlyList<string> scope, string path) => scope.Any(entry => IngestPath.IsWithinScope(path, entry));

    private static async Task<string?> ReadSettingAsync(SqliteConnection connection, string key,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key }, cancellationToken))
            .ConfigureAwait(false);

    private bool IsIndexableFile(string path, [NotNullWhen(true)] out IFileTypeHandler? handler)
    {
        if (!IsHidden(path))
        {
            return fileTypeMatcher.TryGetHandler(path, out handler);
        }

        handler = null;
        return false;
    }

    private static bool IsHidden(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.');
    }

    /// <summary>
    ///     Directory-walk form: hidden not just when the leaf itself starts with `.`, but when any
    ///     segment between <paramref name="root" /> and <paramref name="path" /> does (`.git/hooks/
    ///     pre-commit`), or matches the built-in deny set (<see cref="WatchDenySet" /> —
    ///     node_modules/bin/obj/.git/.venv/__pycache__/dist/build/target).
    /// </summary>
    private static bool IsHidden(string root, string path) => IngestPath.HasHiddenOrDeniedSegment(root, path, WatchDenySet.Names);

    /// <summary>`ai-raccoon.ignore` check against the walk root's rules (§2.1): never true for the
    /// ignore file's own path — it is unindexable by extension anyway, but a `*` pattern must not
    /// hide it from the caller's own re-scan/reconcile bookkeeping.</summary>
    private static bool IsIgnored(IgnoreRules rules, string root, string path)
    {
        if (!rules.HasRules)
        {
            return false;
        }

        if (string.Equals(Path.GetFileName(path), IgnoreRulesProvider.FileName, StringComparison.Ordinal) &&
            IngestPath.PathComparer.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(root)))
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, path);
        return rules.IsIgnored(relative, isDirectory: false);
    }

    private static CommandDefinition Def(string sql, object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(sql, parameters, cancellationToken: cancellationToken);

    /// <summary>The leaf of the chunk's heading trail, or null when it has no heading.</summary>
    private static string? HeadingSection(string chunk)
    {
        var headingPath = HeadingPathParser.Parse(chunk);
        if (string.IsNullOrWhiteSpace(headingPath))
        {
            return null;
        }

        // HeadingPathParser joins on " > ", so the bare '>' is not a separator: a heading carrying
        // one of its own ("<!-- REQUIRED -->") would otherwise end the split on an empty segment.
        var lastSeparator = headingPath.LastIndexOf(" > ", StringComparison.Ordinal);
        var leaf = (lastSeparator < 0 ? headingPath : headingPath[(lastSeparator + 3)..]).Trim();
        return leaf.Length == 0 ? null : leaf;
    }
}
