using System.CommandLine;
using System.Globalization;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>One-shot settings verb handlers: access modes, embedding model, retrieval alpha, sweep policy.</summary>
public sealed class SettingsCommands
{
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

        // WP3 directory activation (M3/M4): a directory REQUIRES a valid manifest.json, and only
        // 384-dimension manifests are loadable until the dimension-reconcile work lands. Refuse
        // BEFORE the outbox commits — a refused model set must never mark the bank pending.
        if (path is not null && Directory.Exists(path))
        {
            var descriptor = ProvisionalManifestDescriptor.Load(Path.GetFullPath(path));
            ProvisionalManifestDescriptor.RequireWp3Supported(descriptor);
        }

        // A remote API key is meaningless for the local engine; don't leave it in settings.
        await store.DeleteSettingAsync(EmbeddingSettingsKeys.ApiKey, cancellationToken);
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

        await modelMigrations.StartModelMigrationAsync("openai", model, baseUrl, cancellationToken);
        await streams.WriteOutputLineAsync($"embedding engine set to openai:{model}; re-embedding in the background");
        return 0;
    }

    public async Task<int> ModelResetAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        foreach (var key in new[]
                 {
                     EmbeddingSettingsKeys.Provider, EmbeddingSettingsKeys.Model, EmbeddingSettingsKeys.BaseUrl,
                     EmbeddingSettingsKeys.Engine, EmbeddingSettingsKeys.ApiKey
                 })
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        await streams.WriteOutputLineAsync("embedding engine reset to default: no engine (FTS5-only search)");
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
            return 0;
        }

        await streams.WriteOutputLineAsync($"provider: {provider}");
        await streams.WriteOutputLineAsync($"model: {rows.GetValueOrDefault(EmbeddingSettingsKeys.Model) ?? "(unset)"}");
        await streams.WriteOutputLineAsync($"baseUrl: {rows.GetValueOrDefault(EmbeddingSettingsKeys.BaseUrl) ?? "(unset)"}");
        await streams.WriteOutputLineAsync($"engine: {rows.GetValueOrDefault(EmbeddingSettingsKeys.Engine) ?? "(unset)"}");
        var keyState = rows.ContainsKey(EmbeddingSettingsKeys.ApiKey) ? "set" : "unset";
        await streams.WriteOutputLineAsync($"apiKey: {keyState}");
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
