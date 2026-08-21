using System.Diagnostics.CodeAnalysis;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
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
    IEmbeddingService embeddingService,
    IIgnoreRulesProvider? ignoreRulesProvider = null,
    ICodeFileTypeMatcher? codeFileTypeMatcher = null,
    ICodeIngestor? codeIngestor = null,
    IWatchStore? watchStore = null) : IFileIngestor
{
    private readonly ICodeFileTypeMatcher _codeFileTypeMatcher = codeFileTypeMatcher ?? NullCodeFileTypeMatcher.Instance;
    private readonly ICodeIngestor _codeIngestor = codeIngestor ?? NullCodeIngestor.Instance;
    private readonly IIgnoreRulesProvider _ignoreRulesProvider = ignoreRulesProvider ?? NullIgnoreRulesProvider.Instance;
    private readonly IWatchStore _watchStore = watchStore ?? NullWatchStore.Instance;

    /// <summary>
    ///     Set <paramref name="embedInline" /> false when the caller holds a write transaction: embedding
    ///     runs the engine per chunk, and a lock held that long stalls another process's first bank open.
    ///     Ignore rules apply here too, ahead of routing (B2/§2.1: ignore wins for both the memory
    ///     and the code pipeline) — <see cref="ResolveIgnoreRootAsync" /> finds the right root even
    ///     though (unlike <see cref="IngestDirectoryAsync" />) no walk root is given directly.
    /// </summary>
    public async Task<FileIngestResult> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken, bool embedInline = true)
    {
        await RequireInScopeAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);

        if (IsHidden(path))
        {
            return new FileIngestResult(0, true);
        }

        var ignoreRoot = await ResolveIgnoreRootAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);
        var ignoreRules = await _ignoreRulesProvider.LoadAsync(ignoreRoot, cancellationToken).ConfigureAwait(false);
        if (IsIgnored(ignoreRules, ignoreRoot, path))
        {
            return new FileIngestResult(0, true);
        }

        if (!fileTypeMatcher.TryGetHandler(path, out var handler))
        {
            return await IngestAsCodeAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var rows = await InsertChunksAsync(connection, projectId, path, content, handler, context, cancellationToken, embedInline)
            .ConfigureAwait(false);
        return new FileIngestResult(rows, true);
    }

    /// <summary>
    ///     OQ4 (docs/work/2026-08-21-code-search-implementation-plan.md §12.4): explicit single-file
    ///     ingest routes code extensions to the code corpus too. Not a code file at all — fingerprint
    ///     eligible as before, nothing will ever come of it regardless of chunker. A code file the
    ///     chunker produced zero rows for is NOT fingerprint eligible (B1) UNLESS the content is
    ///     empty/whitespace-only (S3): a stand-in chunker (e.g. `NoOpCodeChunker`) producing zero
    ///     rows for real content must not let the watch digest settle into treating the file as
    ///     done, but a genuinely empty/whitespace-only file chunks to zero rows FOREVER regardless
    ///     of chunker quality — refusing to fingerprint it would re-ingest it on every poll forever.
    /// </summary>
    private async Task<FileIngestResult> IngestAsCodeAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken)
    {
        if (!_codeFileTypeMatcher.IsCodeFile(path))
        {
            return new FileIngestResult(0, true);
        }

        var rows = await _codeIngestor.IngestFileAsync(connection, projectId, path, cancellationToken)
            .ConfigureAwait(false);
        if (rows > 0)
        {
            return new FileIngestResult(rows, true);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return new FileIngestResult(0, string.IsNullOrWhiteSpace(content));
    }

    /// <summary>
    ///     B2: the `ai-raccoon.ignore` root for an explicit single-file ingest — the containing
    ///     registered watch if one exists (longest/most specific match), else the ingest-scope
    ///     allowlist entry that admits the path (same tie-break), else the file's own parent
    ///     directory as the last resort. `IgnoreRulesProvider` reads exactly one file at whatever
    ///     root it is given — no nested discovery — so picking the wrong root here silently misses
    ///     the real ignore file instead of erroring.
    /// </summary>
    private async Task<string> ResolveIgnoreRootAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken)
    {
        var normalized = IngestPath.Normalize(path);

        var watches = await _watchStore.ListWatchesAsync(cancellationToken).ConfigureAwait(false);
        var containingWatch = watches
            .Where(w => w.ProjectId == projectId && IngestPath.IsWithinScope(normalized, w.Path))
            .OrderByDescending(w => w.Path.Length)
            .FirstOrDefault();
        if (containingWatch is not null)
        {
            return containingWatch.Path;
        }

        var scope = await ReadScopeAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        var admittingScopeEntry = scope
            .Where(entry => IngestPath.IsWithinScope(normalized, entry))
            .OrderByDescending(entry => entry.Length)
            .FirstOrDefault();
        if (admittingScopeEntry is not null)
        {
            return admittingScopeEntry;
        }

        return Path.GetDirectoryName(path) ?? path;
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
                    indexed += await _codeIngestor.IngestFileAsync(connection, projectId, file, cancellationToken, scope)
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
    private async Task<ChunkSize> ChunkSizeForAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var configured = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Provider }, cancellationToken))
            .ConfigureAwait(false);
        var provider = string.IsNullOrWhiteSpace(configured) ? BundledProvider : configured;

        var model = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.Model }, cancellationToken))
            .ConfigureAwait(false);
        var settings = new EmbeddingSettings(provider, model, null, null);
        var maxTokens = embeddingService.ResolveChunkBudgetFor(settings);
        var overlayTokens = Math.Min(ChunkingDefaults.OverlayTokens, Math.Max(0, maxTokens - 1));
        TokenCount? countTokens = provider.Equals("local", StringComparison.OrdinalIgnoreCase)
            ? new TokenCount(embeddingService.ResolveTokenizer(settings)!.CountTokens)
            : null;
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
