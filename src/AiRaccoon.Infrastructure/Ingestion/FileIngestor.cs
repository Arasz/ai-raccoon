using System.Diagnostics.CodeAnalysis;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.EventPump;
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
    IMemorySourceStore sourceStore,
    TimeProvider timeProvider,
    IEmbeddingService embeddingService,
    IIgnoreRulesProvider ignoreRulesProvider,
    ICodeFileTypeMatcher codeFileTypeMatcher,
    ICodeIngestor codeIngestor,
    IWatchStore watchStore,
    IEventPump<EmbedDrainRequest> embedDrainPump) : IFileIngestor
{
    /// <summary>
    ///     Ignore rules apply ahead of routing, for both the memory and code pipelines —
    ///     <see cref="ResolveIgnoreRootAsync" /> finds the right root. Rows are left <c>pending</c>;
    ///     the caller enqueues the corpus's <see cref="EmbedDrainRequest" /> once it is safe to.
    /// </summary>
    public async Task<FileIngestResult> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken)
    {
        await RequireInScopeAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);

        if (IsHidden(path))
        {
            return new FileIngestResult(0, true);
        }

        var ignoreRoot = await ResolveIgnoreRootAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);
        var ignoreRules = await ignoreRulesProvider.LoadAsync(ignoreRoot, cancellationToken).ConfigureAwait(false);
        if (IsIgnored(ignoreRules, ignoreRoot, path))
        {
            return new FileIngestResult(0, true);
        }

        if (!fileTypeMatcher.TryGetHandler(path, out var handler))
        {
            return await IngestAsCodeAsync(connection, projectId, path, cancellationToken).ConfigureAwait(false);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var (rows, hashes) = await InsertChunksAsync(connection, projectId, path, content, handler, context,
                cancellationToken)
            .ConfigureAwait(false);
        return new FileIngestResult(rows, true, hashes);
    }

    /// <summary>
    ///     Explicit single-file ingest also routes code extensions to the code corpus
    ///     (docs/work/2026-08-21-code-search-implementation-plan.md §12.4). Fingerprint-eligible
    ///     when chunking produced rows, or when zero rows is because the content is
    ///     empty/whitespace-only — never for a stand-in chunker's zero rows on real content.
    /// </summary>
    private async Task<FileIngestResult> IngestAsCodeAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken)
    {
        if (!codeFileTypeMatcher.IsCodeFile(path))
        {
            return new FileIngestResult(0, true);
        }

        var result = await codeIngestor.IngestFileAsync(connection, projectId, path, cancellationToken)
            .ConfigureAwait(false);
        if (result.ChunkHashes is { Count: > 0 })
        {
            return new FileIngestResult(result.Rows, true, CodeChunkHashes: result.ChunkHashes);
        }

        return result.ContentWhitespaceOnly
            ? new FileIngestResult(0, true, CodeChunkHashes: result.ChunkHashes ?? [])
            : new FileIngestResult(0, false, CodeChunkHashes: null);
    }

    /// <summary>
    ///     The `ai-raccoon.ignore` root for an explicit single-file ingest: the containing
    ///     registered watch if one exists (longest match), else the ingest-scope allowlist entry
    ///     that admits the path, else the file's own parent directory. `IgnoreRulesProvider` reads
    ///     one file at whatever root it is given — no nested discovery.
    /// </summary>
    private async Task<string> ResolveIgnoreRootAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken)
    {
        var normalized = IngestPath.Normalize(path);

        var watches = await watchStore.ListWatchesAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<DirectoryIngestResult> IngestDirectoryAsync(SqliteConnection connection, string projectId,
        string path, string? context, CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        RequireInScope(scope, path);

        var ignoreRules = await ignoreRulesProvider.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => !IsHidden(path, file) && !IsIgnored(ignoreRules, path, file) && IsInScope(scope, file))
            .OrderBy(file => file, StringComparer.Ordinal);

        var indexed = 0;
        var memoryRowsWritten = false;
        var codeRowsWritten = false;
        var walked = new List<WalkedFile>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fileTypeMatcher.TryGetHandler(file, out var handler))
            {
                if (codeFileTypeMatcher.IsCodeFile(file))
                {
                    var codeResult = await codeIngestor.IngestFileAsync(connection, projectId, file, cancellationToken, scope)
                        .ConfigureAwait(false);
                    indexed += codeResult.Rows;
                    codeRowsWritten |= codeResult.Rows > 0;
                }

                continue;
            }

            var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var (rows, hashes) = await InsertChunksAsync(connection, projectId, file, content, handler, context,
                    cancellationToken)
                .ConfigureAwait(false);
            indexed += rows;
            memoryRowsWritten |= rows > 0;
            walked.Add(new WalkedFile(file, hashes));
        }

        // One signal per corpus the walk actually wrote rows for — never per file (a per-file
        // generator call, serially, on the shared inference session was a third uncoordinated
        // consumer of the pool alongside the two maintenance jobs).
        if (memoryRowsWritten)
        {
            embedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory));
        }

        if (codeRowsWritten)
        {
            embedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Code));
        }

        return new DirectoryIngestResult(indexed, walked);
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

    /// <summary>Leaves every freshly inserted row `embed_state = 'pending'` — the caller enqueues
    /// the corpus's drain once it is safe to (see <see cref="IngestDirectoryAsync" />).</summary>
    private async Task<(int Rows, List<string> Hashes)> InsertChunksAsync(SqliteConnection connection,
        string projectId, string path, string content, IFileTypeHandler handler, string? context,
        CancellationToken cancellationToken)
    {
        var resolvedContext = context ?? ContextNaming.ProjectContext(projectId);
        var bucket = EntryBucket.For(resolvedContext, projectId);
        var (chunkMaxTokens, chunkOverlayTokens, chunkCountTokens) = await ChunkSizeForAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var chunks = handler.Chunker.Chunk(content, chunkMaxTokens, chunkOverlayTokens, chunkCountTokens);
        var hashes = new List<string>(chunks.Count);
        if (chunks.Count == 0)
        {
            return (0, hashes);
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
            hashes.Add(hash);
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

            if (existingId is null)
            {
                // InsertEntry is ON CONFLICT DO NOTHING: affected rows already says whether this
                // call won the insert or a concurrent same-bucket insert got there first — no
                // follow-up SELECT needed to find out.
                var affected = await connection.ExecuteAsync(
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
                if (affected > 0)
                {
                    inserted++;
                }

                continue;
            }

            var chunkId = existingId.Value;
            await connection.ExecuteAsync(
                    Def(MemorySql.SetChunkPosition, new { id = chunkId, chunkIndex = ordinal, totalChunks = chunks.Count },
                        cancellationToken))
                .ConfigureAwait(false);
        }

        return (inserted > 0 ? 1 : 0, hashes);
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
    private static bool IsHidden(string root, string path) => WatchDenySet.Excludes(root, path);

    /// <summary>`ai-raccoon.ignore` check against the walk root's rules: never true for the ignore
    /// file's own path — a `*` pattern must not hide it from the caller's own re-scan/reconcile
    /// bookkeeping.</summary>
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
