using System.ComponentModel;
using System.Runtime.InteropServices;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>Reads one hash from the explicitly selected entry type.</summary>
public sealed class EntryTools(IMemoryStore store, ICodeSearchService codeSearch, IToolGate gate, IEntryKindClassifier classifier)
{
    private const string TnGetEntry = "get_entry";

    [McpServerTool(Name = TnGetEntry)]
    [McpToolScope(ToolScope.All)]
    [Description("Reads one entry by hash from the required single entryType: memory, file, or code. A hash absent from the selected type is unknown-hash; other types are never used as fallback.")]
    public async Task<ApiEnvelope<GetEntryResult>> GetEntry(
        [Description("The project id; every memory operation is scoped to a project.")] [Optional] [DefaultParameterValue("")]
        string projectId,
        [Description("The content hash to read.")]
        string hash,
        [Description("Required single entry type: memory, file, or code. Composite types are refused.")]
        string entryType,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, TnGetEntry, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        EntryType requested;
        try
        {
            requested = EntryTypeParameter.Single(EntryTypeParameter.Parse(entryType));
        }
        catch (ArgumentException ex)
        {
            throw new McpException($"invalid-params: {ex.Message}");
        }

        GetEntryResult result;
        if (requested == EntryType.Code)
        {
            var code = await codeSearch.GetAsync(canonical, hash, cancellationToken)
                       ?? throw new UnknownHashException(hash, canonical);
            result = new GetEntryResult(code.Hash, code.Value, code.Path, null, 0, code.LineStart,
                code.LineEnd, SearchKindWireNames.Code, SearchKindWireNames.Code, EntryTypeParameter.Code);
        }
        else
        {
            var kind = requested == EntryType.Memory ? EntryKind.Memory : EntryKind.File;
            var entry = await store.GetAsync(canonical, hash, cancellationToken, kind)
                        ?? throw new UnknownHashException(hash, canonical);
            result = new GetEntryResult(entry.Hash, entry.Value, entry.Path, entry.Context, entry.CreatedAt,
                null, null, SearchKindWireNames.Memory, SearchKindWireNames.Memory, classifier.ToStoredValue(kind));
        }

        return await gate.WrapAsync(canonical, result, cancellationToken);
    }

    /// <summary>
    ///     One shape for both corpora. The code-only fields are null for a memory entry and the
    ///     context and creation time are null for a code chunk — a code chunk has no memory
    ///     context and no creation timestamp of its own.
    ///     <para>
    ///         <c>Corpus</c> names the corpus that owns the hash: memory or code. The deprecated
    ///         <c>EntryType</c> repeats it until 3.0.0 (ADR-0147).
    ///     </para>
    ///     <c>EntryKind</c> names the selected taxonomy: memory, file, or code.
    /// </summary>
    public sealed record GetEntryResult(
        string Hash,
        string Value,
        string Path,
        string? Context,
        long? CreatedAt,
        int? LineStart,
        int? LineEnd,
        [property: Description("The corpus that owns the hash: memory or code.")]
        string Corpus,
        [property: Description("Deprecated: same as corpus; removed in 3.0.0. Use entryKind for the kind.")]
        string EntryType,
        string EntryKind);
}
