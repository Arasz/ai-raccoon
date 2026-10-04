using System.Net;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;
using AiRaccoon.Hosting.Node;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Settings;

/// <summary>The settings server could not be reached; nothing was read or written. <see cref="Code" /> names the case.</summary>
internal sealed class SettingsServerUnavailableException(int code, string message, Exception? inner = null) : Exception(message, inner)
{
    public int Code { get; } = code;
}

/// <summary>The settings server answered but refused the credential.</summary>
internal sealed class SettingsServerRefusedException(string message) : Exception(message);

/// <summary>The settings server answered but failed processing the request (5xx) — a server-side fault, not a bad argument.</summary>
internal sealed class SettingsServerErrorException(string message) : Exception(message);

/// <summary>
///     Reaches settings through the server rather than the bank (ADR-0075), so a CLI process never
///     writes the bank. Same <see cref="ISettingsStore" /> surface as the bank-backed store, so a
///     subsystem keeps one implementation and only the transport under it changes.
///     <para>
///         Also <see cref="IModelMigrationStore" /> (ADR-0076), <see cref="ICodeEngineStore" />
///         (§3.3 D-E9), <see cref="IRepairStore" />, <see cref="IPromotionQueuePruneStore" />,
///         <see cref="IMaintenanceStatsStore" />, <see cref="INoiseSummaryStore" /> and
///         <see cref="IWatchRegisteredStore" /> (all this same amendment): <c>model embedding set</c>,
///         <c>model code set local</c>, <c>repair</c>, <c>extract prune</c>,
///         <c>settings maintenance list</c>, <c>noise entries</c> and <c>watch registered</c> all
///         reach the same way, over the same connection — one class, one credential, one transport
///         for every control-plane resource. <see cref="IProjectDirectory" /> serves <c>project id</c>.
///     </para>
/// </summary>
internal sealed class ServerSettingsStore : ISettingsStore, IModelMigrationStore, ICodeEngineStore, IRepairStore,
    IPromotionQueuePruneStore, IMaintenanceStatsStore, INoiseSummaryStore, IWatchRegisteredStore, IProjectDirectory
{
    private readonly HttpClient _client;
    private readonly TimeSpan _requestDeadline;

    /// <summary>
    ///     <paramref name="requestDeadline" /> bounds each ordinary settings call; a repair report runs
    ///     without it. The client's own <see cref="HttpClient.Timeout" /> still applies, so its owner sets it.
    /// </summary>
    public ServerSettingsStore(HttpClient client, string token, TimeSpan requestDeadline)
    {
        Guard.IsNotNull(client);
        Guard.IsNotNullOrWhiteSpace(token);
        _client = client;
        _requestDeadline = requestDeadline;
        _client.DefaultRequestHeaders.Remove(McpTokenGate.HeaderName);
        _client.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, token);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(key);
        var response = await SendAsync(token => _client.GetAsync(SettingsProtocol.ForKey(key), token), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Ensure(response);
        var value = await response.Content.ReadFromJsonAsync<SettingValue>(cancellationToken);
        return value?.Value;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(prefix);
        var response = await SendAsync(token => _client.GetAsync(SettingsProtocol.ForPrefix(prefix), token), cancellationToken);
        Ensure(response);
        var rows = await response.Content.ReadFromJsonAsync<SettingRows>(cancellationToken);
        return rows?.Rows ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(key);
        var response = await SendAsync(token =>
            _client.PutAsJsonAsync(SettingsProtocol.Path, new SettingWrite(key, value), token), cancellationToken);
        Ensure(response);
    }

    public async Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(key);
        var response = await SendAsync(token => _client.DeleteAsync(SettingsProtocol.ForKey(key), token), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // Mirror of StartModelMigrationAsync: the endpoint's 409 body is the refusal reason.
            throw new ModelMigrationInProgressException(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        Ensure(response);
    }

    /// <inheritdoc />
    public async Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(provider);
        var response = await SendAsync(token =>
            _client.PostAsJsonAsync(SettingsProtocol.ModelPath, new ModelMigrationRequest(provider, model, baseUrl),
                token), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ModelMigrationInProgressException(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        Ensure(response);
        var body = await response.Content.ReadFromJsonAsync<ModelMigrationResponse>(cancellationToken);
        return new EmbeddingConfig(body!.Provider, body.Model, body.Engine);
    }

    /// <summary>Never called from the CLI — no command asks "is a migration open" (ADR-0076: no progress channel); only ToolGate, server-side, needs this.</summary>
    public Task<bool> HasOpenModelMigrationAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "ai-raccoon: HasOpenModelMigrationAsync is a server-side check (ToolGate); the CLI never calls it");

    /// <inheritdoc />
    public async Task<EmbeddingConfig> ActivateCodeEngineAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(directory);
        var response = await SendAsync(token =>
            _client.PostAsJsonAsync(SettingsProtocol.ModelCodePath, new ModelCodeActivationRequest(directory),
                token), cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new CodeEngineActivationRefusedException(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        Ensure(response);
        var body = await response.Content.ReadFromJsonAsync<ModelCodeActivationResponse>(cancellationToken);
        return new EmbeddingConfig("local", body!.Model, body.Engine);
    }

    /// <inheritdoc />
    public async Task<ReingestRepairReport> ReportReingestAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(token => _client.GetAsync(RepairProtocol.ForKind(RepairKinds.Reingest), token), cancellationToken,
            Timeout.InfiniteTimeSpan);
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<ReingestRepairReport>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<ChunkIndexRepairReport> ReportChunkIndexAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(token => _client.GetAsync(RepairProtocol.ForKind(RepairKinds.ChunkIndex), token), cancellationToken,
            Timeout.InfiniteTimeSpan);
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<ChunkIndexRepairReport>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<ProjectIdCensusReport> ReportProjectIdsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(token => _client.GetAsync(RepairProtocol.ForKind(RepairKinds.ProjectIds), token), cancellationToken,
            Timeout.InfiniteTimeSpan);
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<ProjectIdCensusReport>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task RequestRepairAsync(RepairKind kind, CancellationToken cancellationToken = default, string? projectIdsMapJson = null)
    {
        var response = await SendAsync(token =>
            _client.PostAsJsonAsync(RepairProtocol.Path, new RepairRequest(kind.ToKey(), projectIdsMapJson), token), cancellationToken);
        Ensure(response);
    }

    /// <inheritdoc />
    public async Task<PromotionQueueOrphanReport> ReportPruneOrphansAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(token => _client.GetAsync(PromotionQueuePruneProtocol.Path, token), cancellationToken);
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<PromotionQueueOrphanReport>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task RequestPruneOrphansAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(token =>
            _client.PostAsync(PromotionQueuePruneProtocol.Path, null, token), cancellationToken);
        Ensure(response);
    }

    /// <inheritdoc />
    public async Task<BankStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(token => _client.GetAsync(MaintenanceStatsProtocol.Path, token), cancellationToken);
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<BankStats>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<NoiseEntrySummary> SummarizeAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(token => _client.GetAsync(NoiseSummaryProtocol.Path, token), cancellationToken);
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<NoiseEntrySummary>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(token => _client.GetAsync(WatchRegisteredProtocol.Path, token), cancellationToken);
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<List<WatchRegistration>>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<ProjectRegistration> RegisterAsync(string projectId, string? name, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        var response = await SendAsync(token =>
            _client.PostAsJsonAsync(ProjectsProtocol.Path, new ProjectRegisterRequest(projectId, name), token), cancellationToken);
        Ensure(response);
        var body = (await response.Content.ReadFromJsonAsync<ProjectRegisterResponse>(cancellationToken))!;
        return new ProjectRegistration(body.ProjectId, body.Outcome);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(name);
        var response = await SendAsync(token => _client.GetAsync(ProjectsProtocol.ForName(name), token), cancellationToken);
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<ProjectIdsResponse>(cancellationToken))!.Ids;
    }

    /// <inheritdoc />
    public async Task<ProjectIdCheck> CheckAsync(string projectId, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        var response = await SendAsync(token => _client.GetAsync(ProjectsProtocol.ForCheck(projectId), token), cancellationToken);
        Ensure(response);
        var body = (await response.Content.ReadFromJsonAsync<ProjectCheckResponse>(cancellationToken))!;
        return new ProjectIdCheck(body.ProjectId, body.Status);
    }

    /// <summary>
    ///     A transport failure, or no answer within <paramref name="deadline" /> (the request deadline
    ///     by default), is reported as unavailable rather than surfacing a bare HttpRequestException: a
    ///     caller has to be able to tell "no server answered" — where a write certainly did not land —
    ///     from "the server said no". The caller's own cancellation still surfaces as cancellation.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken, TimeSpan? deadline = null)
    {
        var limit = deadline ?? _requestDeadline;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(limit);
        try
        {
            return await send(bounded.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new SettingsServerUnavailableException(ErrorCode.Reach.StoppedAnswering,
                $"ai-raccoon: no settings server answered at {_client.BaseAddress} ({ex.Message})", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SettingsServerUnavailableException(ErrorCode.Reach.StoppedAnswering,
                $"ai-raccoon: no settings server answered at {_client.BaseAddress} within {limit.TotalSeconds:0.###}s", ex);
        }
    }

    private void Ensure(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SettingsServerRefusedException(
                $"ai-raccoon: the settings server at {_client.BaseAddress} refused this credential — it may serve another data root");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SettingsServerErrorException(
                $"ai-raccoon: the settings server at {_client.BaseAddress} failed with {(int)response.StatusCode} {response.ReasonPhrase} — this is a server-side fault, not a bad argument");
        }

        response.EnsureSuccessStatusCode();
    }
}
