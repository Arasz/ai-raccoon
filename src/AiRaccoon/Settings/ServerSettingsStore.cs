using System.Net;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Watch;
using AiRaccoon.Hosting.Node;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Settings;

/// <summary>The settings server could not be reached; nothing was read or written.</summary>
internal sealed class SettingsServerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

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
///         for every control-plane resource.
///     </para>
/// </summary>
internal sealed class ServerSettingsStore : ISettingsStore, IModelMigrationStore, ICodeEngineStore, IRepairStore,
    IPromotionQueuePruneStore, IMaintenanceStatsStore, INoiseSummaryStore, IWatchRegisteredStore
{
    private readonly HttpClient _client;

    public ServerSettingsStore(HttpClient client, string token)
    {
        Guard.IsNotNull(client);
        Guard.IsNotNullOrWhiteSpace(token);
        _client = client;
        _client.DefaultRequestHeaders.Remove(McpTokenGate.HeaderName);
        _client.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, token);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(key);
        var response = await SendAsync(() => _client.GetAsync(SettingsProtocol.ForKey(key), cancellationToken));
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
        var response = await SendAsync(() => _client.GetAsync(SettingsProtocol.ForPrefix(prefix), cancellationToken));
        Ensure(response);
        var rows = await response.Content.ReadFromJsonAsync<SettingRows>(cancellationToken);
        return rows?.Rows ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(key);
        var response = await SendAsync(() =>
            _client.PutAsJsonAsync(SettingsProtocol.Path, new SettingWrite(key, value), cancellationToken));
        Ensure(response);
    }

    public async Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(key);
        var response = await SendAsync(() => _client.DeleteAsync(SettingsProtocol.ForKey(key), cancellationToken));
        Ensure(response);
    }

    /// <inheritdoc />
    public async Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(provider);
        var response = await SendAsync(() =>
            _client.PostAsJsonAsync(SettingsProtocol.ModelPath, new ModelMigrationRequest(provider, model, baseUrl),
                cancellationToken));
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
        var response = await SendAsync(() =>
            _client.PostAsJsonAsync(SettingsProtocol.ModelCodePath, new ModelCodeActivationRequest(directory),
                cancellationToken));
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
        var response = await SendAsync(() => _client.GetAsync(RepairProtocol.ForKind(RepairKinds.Reingest), cancellationToken));
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<ReingestRepairReport>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<ChunkIndexRepairReport> ReportChunkIndexAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(() => _client.GetAsync(RepairProtocol.ForKind(RepairKinds.ChunkIndex), cancellationToken));
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<ChunkIndexRepairReport>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task RequestRepairAsync(RepairKind kind, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(() =>
            _client.PostAsJsonAsync(RepairProtocol.Path, new RepairRequest(kind.ToKey()), cancellationToken));
        Ensure(response);
    }

    /// <inheritdoc />
    public async Task<PromotionQueueOrphanReport> ReportPruneOrphansAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(() => _client.GetAsync(PromotionQueuePruneProtocol.Path, cancellationToken));
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<PromotionQueueOrphanReport>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task RequestPruneOrphansAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(() =>
            _client.PostAsync(PromotionQueuePruneProtocol.Path, null, cancellationToken));
        Ensure(response);
    }

    /// <inheritdoc />
    public async Task<BankStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(() => _client.GetAsync(MaintenanceStatsProtocol.Path, cancellationToken));
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<BankStats>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<NoiseEntrySummary> SummarizeAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(() => _client.GetAsync(NoiseSummaryProtocol.Path, cancellationToken));
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<NoiseEntrySummary>(cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(() => _client.GetAsync(WatchRegisteredProtocol.Path, cancellationToken));
        Ensure(response);
        return (await response.Content.ReadFromJsonAsync<List<WatchRegistration>>(cancellationToken))!;
    }

    /// <summary>
    ///     A transport failure is reported as unavailable rather than surfacing a bare
    ///     HttpRequestException: a caller has to be able to tell "no server answered" — where a write
    ///     certainly did not land — from "the server said no".
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new SettingsServerUnavailableException(
                $"ai-raccoon: no settings server answered at {_client.BaseAddress} ({ex.Message})", ex);
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
