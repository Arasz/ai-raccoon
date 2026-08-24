using AiRaccoon.Infrastructure.Embedding.Manifest;
using System.CommandLine;
using System.Globalization;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Download;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>One-shot settings verb handlers: access modes, embedding model, retrieval alpha, sweep policy.</summary>
public sealed class SettingsCommands(IRemoteDimensionProbe? dimensionProbe = null)
{
    /// <summary>The dimension a remote engine is assumed to return when it declares none.</summary>
    private const int DefaultRemoteDimensions = 384;

    public async Task<int> AccessDefaultSetAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var mode = parseResult.GetValue<string>("mode");
        if (AccessModePolicy.Parse(mode) is not { } parsed)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: invalid access mode '{mode}' (expected ro, rw or full)");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(AccessModePolicy.GlobalSettingKey, AccessModePolicy.Serialize(parsed), cancellationToken);
        await streams.WriteOutputLineAsync($"access default set to {AccessModePolicy.Serialize(parsed)}");
        return 0;
    }

    public async Task<int> AccessDefaultShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var raw = await store.GetSettingAsync(AccessModePolicy.GlobalSettingKey, cancellationToken);
        await streams.WriteOutputLineAsync(AccessModePolicy.Serialize(AccessModePolicy.Parse(raw) ?? AccessMode.Rw));
        return 0;
    }

    public async Task<int> AccessSetAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var projectId = parseResult.GetValue<string>("project-id")!;
        var mode = parseResult.GetValue<string>("mode")!;
        if (AccessModePolicy.Parse(mode) is not { } parsed)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: invalid access mode '{mode}' (expected ro, rw or full)");
            return ExitCode.InvalidArgument;
        }

        // The global row IS the wildcard for access (findings): `access set *` is spelled
        // `access default set`.
        if (projectId == "*")
        {
            await store.SetSettingAsync(AccessModePolicy.GlobalSettingKey, AccessModePolicy.Serialize(parsed), cancellationToken);
            await streams.WriteOutputLineAsync($"access default set to {AccessModePolicy.Serialize(parsed)}");
        }
        else
        {
            await store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(projectId), AccessModePolicy.Serialize(parsed),
                cancellationToken);
            await streams.WriteOutputLineAsync($"access set to {AccessModePolicy.Serialize(parsed)} for project {projectId}");
        }

        return 0;
    }

    public async Task<int> AccessUnsetAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var projectId = parseResult.GetValue<string>("project-id")!;
        var key = projectId == "*" ? AccessModePolicy.GlobalSettingKey : AccessModePolicy.ProjectSettingKey(projectId);
        await store.DeleteSettingAsync(key, cancellationToken);
        var target = projectId == "*" ? "default" : $"project {projectId}";
        await streams.WriteOutputLineAsync($"access unset for {target}");
        return 0;
    }

    public async Task<int> AccessListAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var rows = await store.GetSettingsByPrefixAsync("access.mode.", cancellationToken);
        var global = rows.TryGetValue(AccessModePolicy.GlobalSettingKey, out var raw)
            ? AccessModePolicy.Serialize(AccessModePolicy.Parse(raw) ?? AccessMode.Rw)
            : "rw";
        await streams.WriteOutputLineAsync($"default: {global}");
        foreach (var (key, value) in rows.Where(kv => kv.Key != AccessModePolicy.GlobalSettingKey).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var projectId = key["access.mode.project:".Length..];
            await streams.WriteOutputLineAsync($"{projectId}: {value}");
        }

        return 0;
    }

    public async Task<int> ModelSetLocalAsync(ParseResult parseResult, IMemoryStore store,
        IModelMigrationStore modelMigrations, StandardStreams streams, CancellationToken cancellationToken)
    {
        var path = parseResult.GetResult("path") is not null ? ExpandTilde(parseResult.GetValue<string>("path")) : null;

        // Directory activation (M3): a directory REQUIRES a valid manifest, validated BEFORE the
        // outbox commits — a refused model set must never mark the bank pending. Any dimension is
        // accepted; the drain reconciles vec0 to it as its first phase (WP4/D3).
        if (path is not null && Directory.Exists(path))
        {
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()).Load(Path.GetFullPath(path));
        }

        // A remote API key and a remote dimension are both meaningless for the local engine; don't
        // leave either in settings, or a 384 local model inherits the last remote model's dims (D2).
        await store.DeleteSettingAsync(EmbeddingSettingsKeys.ApiKey, cancellationToken);
        await store.DeleteSettingAsync(EmbeddingSettingsKeys.Dimensions, cancellationToken);
        await modelMigrations.StartModelMigrationAsync("local", path, null, cancellationToken);
        var modelLabel = path ?? "bundled ONNX model";
        // ADR-0076: the outbox commits synchronously; the re-embed itself runs on the server's
        // maintenance loop, never reported here (ruled: no progress channel).
        await streams.WriteOutputLineAsync(
            $"embedding engine set to local ({modelLabel}); re-embedding in the background");
        return 0;
    }

    public async Task<int> ModelSetOpenAiAsync(ParseResult parseResult, IMemoryStore store,
        IModelMigrationStore modelMigrations, StandardStreams streams, CancellationToken cancellationToken)
    {
        var model = parseResult.GetValue<string>("model");
        var baseUrl = parseResult.GetResult("base-url") is not null ? parseResult.GetValue<string>("base-url") : null;
        var apiKey = parseResult.GetResult("--api-key") is not null ? parseResult.GetValue<string>("--api-key") : null;

        // The key is persisted in settings; write it before the engine so the migration's own
        // re-embed can resolve it once the relay picks the row up.
        if (apiKey is not null)
        {
            await store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, apiKey, cancellationToken);
        }
        else if (string.IsNullOrWhiteSpace(await store.GetSettingAsync(EmbeddingSettingsKeys.ApiKey, cancellationToken)))
        {
            await streams.WriteErrorLineAsync(
                "ai-raccoon: warning — no API key set; run 'ai-raccoon model set openai <model> --api-key <key>' or embeddings will fail");
        }

        var dims = parseResult.GetResult("--dims") is not null ? parseResult.GetValue<int?>("--dims") : null;
        dims = await ProbeDimensionsAsync(model!, baseUrl, apiKey, dims, cancellationToken);

        // Written before the outbox commits: the relay reconciles vec0 to this dimension as its
        // first drain phase, so the row has to be readable by the time the migration opens (D2/D3).
        if (dims is not null)
        {
            await store.SetSettingAsync(EmbeddingSettingsKeys.Dimensions,
                dims.Value.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }

        await modelMigrations.StartModelMigrationAsync("openai", model, baseUrl, cancellationToken);
        await streams.WriteOutputLineAsync($"embedding engine set to openai:{model}; re-embedding in the background");
        return 0;
    }

    /// <summary>
    ///     D10: refuse a dimension the endpoint contradicts, and refuse silence when the endpoint is
    ///     not 384 — both BEFORE the outbox commits. Returns the dimension to persist, or null when
    ///     the endpoint is the legacy 384 shape and nothing was declared.
    /// </summary>
    private async Task<int?> ProbeDimensionsAsync(string model, string? baseUrl, string? apiKey, int? declared,
        CancellationToken cancellationToken)
    {
        if (dimensionProbe is null)
        {
            return declared;
        }

        int probed;
        try
        {
            probed = await dimensionProbe.ProbeAsync(model, baseUrl, apiKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The embedding endpoint for '{model}' could not be reached, so its output dimension is unknown: " +
                $"{ex.Message}. Fix the endpoint or the API key and re-run; nothing has been changed.", ex);
        }

        if (declared is not null && declared.Value != probed)
        {
            throw new InvalidOperationException(
                $"Declared --dims {declared.Value} but '{model}' returns {probed}-dimension embeddings. " +
                $"Re-run with --dims {probed}, or point at a model that matches; nothing has been changed.");
        }

        if (declared is null && probed != DefaultRemoteDimensions)
        {
            throw new InvalidOperationException(
                $"'{model}' returns {probed}-dimension embeddings, not the assumed {DefaultRemoteDimensions}. " +
                $"Re-run with --dims {probed} so the bank's vector index can be rebuilt to match.");
        }

        return declared;
    }

    /// <summary>
    ///     §3.3 D-E9: the CLI's fast pre-flight before the store's own checks (the store is the
    ///     real gate for the HTTP path). Any manifest dimension is accepted — vec_code is
    ///     reconciled to it by the store (vec-code-unfix-dim). A missing/invalid manifest
    ///     surfaces the loader's own actionable error unchanged.
    /// </summary>
    public Task<int> ModelSetCodeLocalAsync(ParseResult parseResult, ICodeEngineStore codeEngine,
        StandardStreams streams, CancellationToken cancellationToken) =>
        ActivateCodeDirectoryAsync(Path.GetFullPath(ExpandTilde(parseResult.GetValue<string>("dir"))!),
            codeEngine, streams, cancellationToken);

    /// <summary>
    ///     #422: `model set code default` is the one command every "no code engine" surface quotes.
    ///     It downloads <see cref="CodeEngineSetup.DefaultModelRepoId" /> into the usual
    ///     <c>&lt;data-root&gt;/models/&lt;slug&gt;</c> when it is not already there, then activates
    ///     it — deliberately both halves, because a hint that leaves the reader to construct a path
    ///     for a second command is one nobody completes. An already-downloaded directory is
    ///     re-activated without re-fetching 187 MB.
    /// </summary>
    internal async Task<int> ModelSetCodeDefaultAsync(ModelDownloadCommands modelDownload,
        ICodeEngineStore codeEngine, string dataRoot, StandardStreams streams, CancellationToken cancellationToken)
    {
        Guard.IsNotNullOrWhiteSpace(dataRoot);
        var targetDir = Path.GetFullPath(Path.Combine(dataRoot, "models",
            ModelSlug.Sanitize(CodeEngineSetup.DefaultModelRepoId)));

        if (File.Exists(Path.Combine(targetDir, EmbeddingManifest.FileName)))
        {
            await streams.WriteOutputLineAsync(
                $"{CodeEngineSetup.DefaultModelRepoId} is already downloaded at {targetDir}; activating it");
        }
        else
        {
            var exit = await modelDownload.DownloadDefaultCodeModelAsync(targetDir, streams, cancellationToken);
            if (exit != ExitCode.Success)
            {
                return exit;
            }
        }

        return await ActivateCodeDirectoryAsync(targetDir, codeEngine, streams, cancellationToken);
    }

    private async Task<int> ActivateCodeDirectoryAsync(string fullPath, ICodeEngineStore codeEngine,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        // The manifest pre-flight: a missing/invalid manifest surfaces the loader's own message.
        // Dimensions are deliberately NOT checked here (vec-code-unfix-dim) — the store accepts
        // any manifest dimension and reconciles vec_code to it.
        new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())
            .Load(fullPath);

        await codeEngine.ActivateCodeEngineAsync(fullPath, cancellationToken);
        await streams.WriteOutputLineAsync(
            $"code embedding engine set to local ({fullPath}); the code-reindex maintenance job will re-embed pending rows");
        return 0;
    }

    public async Task<int> ModelResetAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        foreach (var key in new[]
                 {
                     EmbeddingSettingsKeys.Provider, EmbeddingSettingsKeys.Model, EmbeddingSettingsKeys.BaseUrl,
                     EmbeddingSettingsKeys.Engine, EmbeddingSettingsKeys.ApiKey,
                     EmbeddingSettingsKeys.Dimensions
                 })
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        await streams.WriteOutputLineAsync("embedding engine reset to default: no engine (FTS5-only search)");
        return 0;
    }

    /// <summary>WP11-A/G16: 0 = ORT's own default; takes effect on the next server restart (sessions are cached per fingerprint).</summary>
    public async Task<int> ModelThreadsSetAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("n")!;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threads) || threads < 0)
        {
            await streams.WriteErrorLineAsync(
                $"ai-raccoon: invalid threads '{raw}' (expected a non-negative integer; 0 = ORT default)");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(EmbeddingSettingsKeys.Threads, threads.ToString(CultureInfo.InvariantCulture), cancellationToken);
        // G4: the same phrase doctor and `model show` render for the same stored value.
        // ThreadCountSource(raw) always resolves to "setting" here — raw just passed the explicit-value
        // parse above — but it goes through the shared helper anyway so this line can never drift.
        await streams.WriteOutputLineAsync(
            $"embedding threads set to {EmbeddingService.ThreadCountDisplay(threads)} ({EmbeddingService.ThreadCountSource(raw)}); takes effect on the next server restart");
        return 0;
    }

    /// <summary>§3.3: `settings model code reset` deletes ONLY the code rows — the memory engine's rows are untouched.</summary>
    public async Task<int> ModelCodeResetAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        await store.DeleteSettingAsync(EmbeddingSettingsKeys.CodeModel, cancellationToken);
        await store.DeleteSettingAsync(EmbeddingSettingsKeys.CodeEngine, cancellationToken);
        await store.DeleteSettingAsync(EmbeddingSettingsKeys.CodeDimensions, cancellationToken);
        await streams.WriteOutputLineAsync("code embedding engine reset to default: no engine (FTS5-only code search)");
        return 0;
    }

    public async Task<int> ModelShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var rows = await store.GetSettingsByPrefixAsync("embedding.", cancellationToken);
        var provider = rows.GetValueOrDefault(EmbeddingSettingsKeys.Provider);
        if (string.IsNullOrWhiteSpace(provider))
        {
            await streams.WriteOutputLineAsync("provider: (none — FTS5-only search)");
        }
        else
        {
            await streams.WriteOutputLineAsync($"provider: {provider}");
            await streams.WriteOutputLineAsync($"model: {rows.GetValueOrDefault(EmbeddingSettingsKeys.Model) ?? "(unset)"}");
            await streams.WriteOutputLineAsync($"baseUrl: {rows.GetValueOrDefault(EmbeddingSettingsKeys.BaseUrl) ?? "(unset)"}");
            await streams.WriteOutputLineAsync($"engine: {rows.GetValueOrDefault(EmbeddingSettingsKeys.Engine) ?? "(unset)"}");
            var keyState = rows.ContainsKey(EmbeddingSettingsKeys.ApiKey) ? "set" : "unset";
            await streams.WriteOutputLineAsync($"apiKey: {keyState}");
        }

        // Independent of provider (WP11-A/G16): the ORT thread cap applies to any local session,
        // memory or code, so it is shown unconditionally like codeModel below. G4: the resolved
        // count and its source, in the same phrase doctor and `model threads` render.
        var (resolvedThreads, threadsSource) =
            EmbeddingService.ResolveThreadCountForDisplay(rows.GetValueOrDefault(EmbeddingSettingsKeys.Threads));
        await streams.WriteOutputLineAsync($"threads: {EmbeddingService.ThreadCountDisplay(resolvedThreads)} ({threadsSource})");

        // Independent of the memory engine (§3.3): shown even when no memory provider is
        // configured, since the code corpus can be activated on its own.
        var codeModel = rows.GetValueOrDefault(EmbeddingSettingsKeys.CodeModel);
        if (string.IsNullOrWhiteSpace(codeModel))
        {
            await streams.WriteOutputLineAsync("codeModel: (none — FTS5-only code search)");
        }
        else
        {
            await streams.WriteOutputLineAsync($"codeModel: {codeModel}");
            await streams.WriteOutputLineAsync($"codeEngine: {rows.GetValueOrDefault(EmbeddingSettingsKeys.CodeEngine) ?? "(unset)"}");
        }

        return 0;
    }

    public async Task<int> RetrievalAlphaSetAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("alpha")!;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha) ||
            alpha is < 0.0 or > 1.0)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: invalid alpha '{raw}' (expected a number in 0..1)");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(SearchParameterSettingsKeys.StructureAlpha,
            alpha.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await streams.WriteOutputLineAsync($"retrieval alpha set to {alpha.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    public async Task<int> RetrievalAlphaShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var raw = await store.GetSettingAsync(SearchParameterSettingsKeys.StructureAlpha, cancellationToken);
        var alpha = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : SearchParameterSettingsKeys.DefaultStructureAlpha;
        await streams.WriteOutputLineAsync(alpha.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    /// <summary>
    ///     The no-fusion-regression reorder's kill switch (docs/adr/0078). Default off, so `enable`
    ///     is how the evidence-gathering path is armed and `disable` returns to the baseline.
    /// </summary>
    public async Task<int> RetrievalFusionSetAsync(bool enabled, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        await store.SetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, enabled ? "true" : "false",
            cancellationToken);
        await streams.WriteOutputLineAsync($"retrieval fusion no-regression reorder {(enabled ? "enabled" : "disabled")}");
        return 0;
    }

    /// <summary>Names the default in the output: reading "False" alone cannot tell an unset bank from a disabled one.</summary>
    public async Task<int> RetrievalFusionShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var enabled = FusionConfigKeys.ParseNoRegressionEnabled(
            await store.GetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, cancellationToken));
        await streams.WriteOutputLineAsync(
            $"enabled: {enabled}  (default: {FusionConfigKeys.DefaultNoRegressionEnabled} — off serves the baseline fusion)");
        return 0;
    }

    // Retrieval options (SearchParameterSettingsKeys): each has set|show, one shared core per
    // value kind. The stored value is the wire form the settings snapshot parses back.

    public Task<int> RetrievalRrfKSetAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        SetRetrievalIntAsync(parseResult, store, streams, SearchParameterSettingsKeys.RrfK, "rrfK", 1, cancellationToken);

    public Task<int> RetrievalRrfKShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        ShowRetrievalOptionAsync(store, streams, SearchParameterSettingsKeys.RrfK, "rrfK",
            SearchParameterSettingsKeys.DefaultRrfK.ToString(CultureInfo.InvariantCulture), cancellationToken);

    public Task<int> RetrievalFtsWeightSetAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        SetRetrievalIntAsync(parseResult, store, streams, SearchParameterSettingsKeys.FtsWeight, "fts-weight", 0,
            cancellationToken);

    public Task<int> RetrievalFtsWeightShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        ShowRetrievalOptionAsync(store, streams, SearchParameterSettingsKeys.FtsWeight, "fts-weight",
            SearchParameterSettingsKeys.DefaultFtsWeight.ToString(CultureInfo.InvariantCulture), cancellationToken);

    public Task<int> RetrievalVectorWeightSetAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        SetRetrievalIntAsync(parseResult, store, streams, SearchParameterSettingsKeys.VectorWeight, "vector-weight", 0,
            cancellationToken);

    public Task<int> RetrievalVectorWeightShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        ShowRetrievalOptionAsync(store, streams, SearchParameterSettingsKeys.VectorWeight, "vector-weight",
            SearchParameterSettingsKeys.DefaultVectorWeight.ToString(CultureInfo.InvariantCulture), cancellationToken);

    public Task<int> RetrievalSourceLambdaSetAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        SetRetrievalDoubleAsync(parseResult, store, streams, SearchParameterSettingsKeys.SourceLambda, "source-lambda",
            0.0, 1.0, cancellationToken);

    public Task<int> RetrievalSourceLambdaShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        ShowRetrievalOptionAsync(store, streams, SearchParameterSettingsKeys.SourceLambda, "source-lambda",
            SearchParameterSettingsKeys.DefaultSourceLambda.ToString(CultureInfo.InvariantCulture), cancellationToken);

    public Task<int> RetrievalConsolidationSetAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        SetRetrievalDoubleAsync(parseResult, store, streams, SearchParameterSettingsKeys.ConsolidationThreshold,
            "consolidation", 0.0, double.MaxValue, cancellationToken);

    public Task<int> RetrievalConsolidationShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        ShowRetrievalOptionAsync(store, streams, SearchParameterSettingsKeys.ConsolidationThreshold, "consolidation",
            SearchParameterSettingsKeys.DefaultConsolidationThreshold.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    public Task<int> RetrievalDocFormulaSetAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        SetRetrievalEnumAsync(parseResult, store, streams, SearchParameterSettingsKeys.DocScoreFormula, "doc-formula",
            ["max", "sum"], cancellationToken);

    public Task<int> RetrievalDocFormulaShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        ShowRetrievalOptionAsync(store, streams, SearchParameterSettingsKeys.DocScoreFormula, "doc-formula",
            SearchParameterSettingsKeys.DefaultDocScoreFormula.ToString().ToLowerInvariant(), cancellationToken);

    public Task<int> RetrievalWindowSetAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        SetRetrievalEnumAsync(parseResult, store, streams, SearchParameterSettingsKeys.CandidateWindow, "window",
            ["max3x100", "max5x50"], cancellationToken);

    public Task<int> RetrievalWindowShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken) =>
        ShowRetrievalOptionAsync(store, streams, SearchParameterSettingsKeys.CandidateWindow, "window",
            SearchParameterSettingsKeys.DefaultCandidateWindow.ToString().ToLowerInvariant(), cancellationToken);

    /// <summary>Prints every retrieval option with its source — one call answers "what does a search run with?".</summary>
    public async Task<int> RetrievalShowAllAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var rows = await store.GetSettingsByPrefixAsync("retrieval.", cancellationToken);
        var fusionRaw = await store.GetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, cancellationToken);

        (string Key, string Name, string Default)[] options =
        [
            (SearchParameterSettingsKeys.RrfK, "rrfK", SearchParameterSettingsKeys.DefaultRrfK.ToString(CultureInfo.InvariantCulture)),
            (SearchParameterSettingsKeys.FtsWeight, "ftsWeight", SearchParameterSettingsKeys.DefaultFtsWeight.ToString(CultureInfo.InvariantCulture)),
            (SearchParameterSettingsKeys.VectorWeight, "vectorWeight", SearchParameterSettingsKeys.DefaultVectorWeight.ToString(CultureInfo.InvariantCulture)),
            (SearchParameterSettingsKeys.SourceLambda, "sourceLambda", SearchParameterSettingsKeys.DefaultSourceLambda.ToString(CultureInfo.InvariantCulture)),
            (SearchParameterSettingsKeys.ConsolidationThreshold, "consolidationThreshold", SearchParameterSettingsKeys.DefaultConsolidationThreshold.ToString(CultureInfo.InvariantCulture)),
            (SearchParameterSettingsKeys.DocScoreFormula, "docScoreFormula", SearchParameterSettingsKeys.DefaultDocScoreFormula.ToString().ToLowerInvariant()),
            (SearchParameterSettingsKeys.CandidateWindow, "candidateWindow", SearchParameterSettingsKeys.DefaultCandidateWindow.ToString().ToLowerInvariant()),
            (SearchParameterSettingsKeys.StructureAlpha, "structureAlpha", SearchParameterSettingsKeys.DefaultStructureAlpha.ToString(CultureInfo.InvariantCulture)),
            (FusionConfigKeys.NoRegressionEnabledGlobal, "fusionNoRegressionEnabled", FusionConfigKeys.DefaultNoRegressionEnabled.ToString().ToLowerInvariant())
        ];

        foreach (var (key, name, fallback) in options)
        {
            var raw = key == FusionConfigKeys.NoRegressionEnabledGlobal ? fusionRaw : rows.GetValueOrDefault(key);
            await streams.WriteOutputLineAsync($"{name}: {raw ?? fallback}  ({(raw is null ? "default" : "setting")})");
        }

        return 0;
    }

    private static async Task<int> SetRetrievalIntAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, string key, string displayName, int min, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("value")!;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min)
        {
            await streams.WriteErrorLineAsync(
                $"ai-raccoon: invalid {displayName} '{raw}' (expected an integer >= {min})");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(key, value.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await streams.WriteOutputLineAsync($"retrieval {displayName} set to {value}");
        return 0;
    }

    private static async Task<int> SetRetrievalDoubleAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, string key, string displayName, double min, double max,
        CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("value")!;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            value < min || value > max)
        {
            await streams.WriteErrorLineAsync(
                $"ai-raccoon: invalid {displayName} '{raw}' (expected a number in {min}..{max})");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(key, value.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await streams.WriteOutputLineAsync($"retrieval {displayName} set to {value.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    private static async Task<int> SetRetrievalEnumAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, string key, string displayName, string[] allowed, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("value")!;
        var normalized = raw.Trim().ToLowerInvariant();
        if (!allowed.Contains(normalized))
        {
            await streams.WriteErrorLineAsync(
                $"ai-raccoon: invalid {displayName} '{raw}' (expected one of: {string.Join(", ", allowed)})");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(key, normalized, cancellationToken);
        await streams.WriteOutputLineAsync($"retrieval {displayName} set to {normalized}");
        return 0;
    }

    private static async Task<int> ShowRetrievalOptionAsync(IMemoryStore store, StandardStreams streams, string key,
        string displayName, string fallbackText, CancellationToken cancellationToken)
    {
        var raw = await store.GetSettingAsync(key, cancellationToken);
        await streams.WriteOutputLineAsync($"{displayName}: {raw ?? fallbackText}  (default: {fallbackText})");
        return 0;
    }

    public async Task<int> SweepThresholdSetAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("threshold")!;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
            !SweepThreshold.IsValid(threshold))
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: invalid threshold '{raw}' (expected a number in 0..1)");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(SweepThreshold.SettingKey, SweepThreshold.Format(threshold), cancellationToken);
        await streams.WriteOutputLineAsync($"sweep threshold set to {SweepThreshold.Format(threshold)}");
        return 0;
    }

    /// <summary>The kill switch for the background reaper; `disable` is the only way to disarm a default-on deleter.</summary>
    public async Task<int> SweepEnabledSetAsync(bool enabled, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        await store.SetSettingAsync(SweepConfigKeys.EnabledGlobal, enabled ? "true" : "false", cancellationToken);
        await streams.WriteOutputLineAsync($"sweep {(enabled ? "enabled" : "disabled")}");
        return 0;
    }

    public async Task<int> SweepIntervalHoursSetAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("hours")!;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) ||
            hours < SweepConfigKeys.MinIntervalHours || hours > SweepConfigKeys.MaxIntervalHours)
        {
            await streams.WriteErrorLineAsync(
                $"ai-raccoon: invalid interval '{raw}' (expected a whole number of hours in {SweepConfigKeys.MinIntervalHours}..{SweepConfigKeys.MaxIntervalHours})");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(SweepConfigKeys.IntervalHoursGlobal,
            hours.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await streams.WriteOutputLineAsync($"sweep interval set to {hours} h");
        return 0;
    }

    /// <summary>The whole policy — a reader must be able to tell whether the reaper is armed, not just its cutoff.</summary>
    public async Task<int> SweepShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var enabled = SweepConfigKeys.ParseEnabled(
            await store.GetSettingAsync(SweepConfigKeys.EnabledGlobal, cancellationToken));
        var hours = SweepConfigKeys.ParseIntervalHours(
            await store.GetSettingAsync(SweepConfigKeys.IntervalHoursGlobal, cancellationToken));
        var threshold = SweepThreshold.Parse(
            await store.GetSettingAsync(SweepThreshold.SettingKey, cancellationToken));
        await streams.WriteOutputLineAsync(
            $"enabled: {enabled}  interval: {hours} h  threshold: {SweepThreshold.Format(threshold)}");
        return 0;
    }

    /// <summary>The kill switch for pre-write noise rejection; `disable` is the only way to disarm a default-on filter.</summary>
    public async Task<int> NoiseEnabledSetAsync(bool enabled, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        await store.SetSettingAsync(NoiseConfigKeys.EnabledGlobal, enabled ? "true" : "false", cancellationToken);
        await streams.WriteOutputLineAsync($"noise rejection {(enabled ? "enabled" : "disabled")}");
        return 0;
    }

    public async Task<int> NoiseShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var enabled = NoiseConfigKeys.ParseEnabled(
            await store.GetSettingAsync(NoiseConfigKeys.EnabledGlobal, cancellationToken));
        await streams.WriteOutputLineAsync($"enabled: {enabled}");
        return 0;
    }

    /// <summary>The kill switch for the read-path query guard (docs/adr/0040); `disable` is the only way to disarm a default-on guard.</summary>
    public async Task<int> QueryGuardEnabledSetAsync(bool enabled, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        await store.SetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, enabled ? "true" : "false", cancellationToken);
        await streams.WriteOutputLineAsync($"query guard {(enabled ? "enabled" : "disabled")}");
        return 0;
    }

    /// <summary>Shadow mode: records what the guard would have done without refusing or annotating anything.</summary>
    public async Task<int> QueryGuardShadowSetAsync(bool shadow, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        await store.SetSettingAsync(QueryGuardConfigKeys.ShadowGlobal, shadow ? "true" : "false", cancellationToken);
        await streams.WriteOutputLineAsync($"query guard shadow mode {(shadow ? "enabled" : "disabled")}");
        return 0;
    }

    /// <summary>The structural detector (docs/adr/0041) ships off; this is the only way to arm it.</summary>
    public async Task<int> QueryGuardStructuralSetAsync(bool enabled, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        await store.SetSettingAsync(QueryGuardConfigKeys.StructuralEnabledGlobal, enabled ? "true" : "false",
            cancellationToken);
        await streams.WriteOutputLineAsync($"query guard structural detector {(enabled ? "enabled" : "disabled")}");
        return 0;
    }

    /// <summary>The score a query must clear before the structural detector annotates it.</summary>
    public async Task<int> QueryGuardStructuralThresholdSetAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("threshold")!;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
            threshold is < 0.0 or > 1.0)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: invalid threshold '{raw}' (expected a number in 0..1)");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(QueryGuardConfigKeys.StructuralThresholdGlobal,
            threshold.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await streams.WriteOutputLineAsync(
            $"query guard structural threshold set to {threshold.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    public async Task<int> QueryGuardShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var enabled = QueryGuardConfigKeys.ParseEnabled(
            await store.GetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, cancellationToken));
        var shadow = QueryGuardConfigKeys.ParseShadow(
            await store.GetSettingAsync(QueryGuardConfigKeys.ShadowGlobal, cancellationToken));
        var structural = QueryGuardConfigKeys.ParseStructuralEnabled(
            await store.GetSettingAsync(QueryGuardConfigKeys.StructuralEnabledGlobal, cancellationToken));
        var threshold = QueryGuardConfigKeys.ParseStructuralThreshold(
            await store.GetSettingAsync(QueryGuardConfigKeys.StructuralThresholdGlobal, cancellationToken));
        await streams.WriteOutputLineAsync($"enabled: {enabled}  shadow: {shadow}  " +
                                           $"structural: {structural}  threshold: {threshold.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    private static string? ExpandTilde(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : path;
    }
}
