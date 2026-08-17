using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Node;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Settings;

/// <summary>The settings server could not be reached; nothing was read or written.</summary>
internal sealed class SettingsServerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The settings server answered but refused the credential.</summary>
internal sealed class SettingsServerRefusedException(string message) : Exception(message);

/// <summary>
///     Reaches settings through the server rather than the bank (ADR-0075), so a CLI process never
///     writes the bank. Same <see cref="ISettingsStore" /> surface as the bank-backed store, so a
///     subsystem keeps one implementation and only the transport under it changes.
///     <para>
///         Also <see cref="IModelMigrationStore" /> (ADR-0076) and <see cref="IRepairStore" />
///         (ADR-0075 amendment): <c>model set</c> and <c>repair</c> reach the same way, over the same
///         connection — one class, one credential, one transport for every control-plane resource.
///     </para>
/// </summary>
internal sealed class ServerSettingsStore : ISettingsStore, IModelMigrationStore, IRepairStore
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

        response.EnsureSuccessStatusCode();
    }
}
