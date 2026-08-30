using System.ComponentModel;
using System.Runtime.InteropServices;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using JetBrains.Annotations;
using ModelContextProtocol.Server;

// ReSharper disable ExplicitCallerInfoArgument

namespace AiRaccoon.Tools;

/// <summary>Thin MCP tool over ICodeSearchService — no business logic here (see .ai-badger/instructions/mcp.instructions.md).</summary>
public sealed class CodeTools(ICodeSearchService codeSearch, IToolGate gate)
{
    private const string TnCodeGet = "code_get";

    [McpServerTool(Name = TnCodeGet)]
    [Description(
        "Reads one code chunk's full source by its content hash, as returned by memory_search kind=code/both. Mirrors memory_get. An unknown hash is refused as unknown-hash.")]
    public async Task<ApiEnvelope<CodeGetResult>> CodeGet(
        [Description("The project id; code is always project-scoped.")]
        [Optional][DefaultParameterValue("")] string projectId,
        [Description("The content hash to read.")]
        string hash,
        CancellationToken cancellationToken = default)
    {
        var canonical = await gate.RequireAsync(projectId, AccessRequirement.Read, TnCodeGet, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        var entry = await codeSearch.GetAsync(canonical, hash, cancellationToken)
                    ?? throw new UnknownHashException(hash, canonical);
        var result = new CodeGetResult(entry.Hash, entry.Value, entry.Path, entry.LineStart, entry.LineEnd);
        var envelope = await gate.WrapAsync(canonical, result, cancellationToken);
        return envelope;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed record CodeGetResult(string Hash, string Value, string Path, int LineStart, int LineEnd);
}
