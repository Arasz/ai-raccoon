using System.ComponentModel;
using System.Runtime.InteropServices;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>
///     One read over the bank's entries, whatever corpus holds them: a note an agent added, a
///     document a file ingested, or a code chunk. The corpus is decided by which store owns the
///     hash, not by the caller — a hash names content, and content-addressed rows can live in
///     more than one corpus (the same bytes as a note and as a code file). When both own it,
///     the read is refused rather than guessed.
/// </summary>
public sealed class EntryTools(IMemoryStore store, ICodeSearchService codeSearch, IToolGate gate)
{
    private const string TnGetEntry = "get_entry";

    [McpServerTool(Name = TnGetEntry)]
    [McpToolScope(ToolScope.All)]
    [Description(
        "Reads one entry's full content by its content hash, as returned by add_memory or query. The entry may be an agent-written memory, an ingested document, or a code chunk — the corpus is the one that owns the hash. A hash no corpus of the project owns is refused as unknown-hash; a hash two corpora own is refused rather than guessed.")]
    public async Task<ApiEnvelope<GetEntryResult>> GetEntry(
        [Description("The project id; every memory operation is scoped to a project.")]
        [Optional][DefaultParameterValue("")] string projectId,
        [Description("The content hash to read.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, TnGetEntry, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        var entry = await store.GetAsync(canonical, hash, cancellationToken);
        var code = await codeSearch.GetAsync(canonical, hash, cancellationToken);

        if (entry is not null && code is not null)
        {
            throw new ArgumentException(
                $"ambiguous-hash: {hash} is both a memory entry and a code chunk; the two corpora hold the same content, so the read is refused rather than guessed.");
        }

        var result = entry is not null
            ? new GetEntryResult(entry.Hash, entry.Value, entry.Path, entry.Context, entry.CreatedAt, null, null, "memory")
            : code is not null
                ? new GetEntryResult(code.Hash, code.Value, code.Path, null, 0, code.LineStart, code.LineEnd, "code")
                : throw new UnknownHashException(hash, canonical);

        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    /// <summary>
    ///     One shape for both corpora. The code-only fields are null for a memory entry and the
    ///     context and creation time are null for a code chunk — a code chunk has no memory
    ///     context and no creation timestamp of its own.
    /// </summary>
    public sealed record GetEntryResult(
        string Hash,
        string Value,
        string Path,
        string? Context,
        long? CreatedAt,
        int? LineStart,
        int? LineEnd,
        string EntryType);
}
