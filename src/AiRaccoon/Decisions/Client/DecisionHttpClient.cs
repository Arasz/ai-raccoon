using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Settings;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Decisions.Client;

/// <summary>
///     Sends decision requests to <c>/decisions/projects/{projectId}</c> over the CLI's authenticated
///     client and reads each answer to its end before returning it.
/// </summary>
internal sealed class DecisionHttpClient : IDecisionClient
{
    /// <summary>A refusal body reaches the terminal; the read stops at this many characters.</summary>
    private const int RefusalBodyMaxLength = 4096;

    private readonly HttpClient _client;
    private readonly TimeSpan _requestDeadline;

    public DecisionHttpClient(HttpClient client, TimeSpan requestDeadline)
    {
        Guard.IsNotNull(client);
        _client = client;
        _requestDeadline = requestDeadline;
    }

    public Task<JsonDocument> RegisterTaskAsync(string projectId, RegisterTaskRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(projectId, "tasks", JsonContent.Create(request), _requestDeadline, cancellationToken);

    public Task<JsonDocument> ExportAsync(string projectId, DecisionTaskSelector? selector, CancellationToken cancellationToken = default) =>
        SendAsync(projectId, "export", JsonContent.Create(new SelectionRequest(selector)), null, cancellationToken);

    public Task<JsonDocument> PreviewAsync(string projectId, DecisionPreviewOptions options, CancellationToken cancellationToken = default) =>
        SendAsync(projectId, "preview", JsonContent.Create(options), _requestDeadline, cancellationToken);

    public Task<JsonDocument> RecordAsync(string projectId, JsonObject body, CancellationToken cancellationToken = default) =>
        SendAsync(projectId, "records", JsonContent.Create(body), _requestDeadline, cancellationToken);

    public Task<JsonDocument> GetAsync(string projectId, string decisionId, bool includeHistory, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(decisionId);
        return SendAsync(projectId, $"records/{Uri.EscapeDataString(decisionId)}?includeHistory={(includeHistory ? "true" : "false")}", null,
            _requestDeadline, cancellationToken);
    }

    public Task<JsonDocument> QueryAsync(string projectId, DecisionQuery query, CancellationToken cancellationToken = default) =>
        SendAsync(projectId, "query", JsonContent.Create(query), _requestDeadline, cancellationToken);

    /// <summary>POSTs <paramref name="body" /> to the operation, or GETs it when there is no body.</summary>
    private async Task<JsonDocument> SendAsync(string projectId, string operation, HttpContent? body, TimeSpan? deadline,
        CancellationToken cancellationToken)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (deadline is { } limit)
        {
            bounded.CancelAfter(limit);
        }

        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
            $"decisions/projects/{Uri.EscapeDataString(projectId)}/{operation}")
        {
            Content = body
        };
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new SettingsServerUnavailableException(ErrorCode.Reach.StoppedAnswering,
                $"ai-raccoon: no server answered at {_client.BaseAddress} ({ex.Message})", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SettingsServerUnavailableException(ErrorCode.Reach.StoppedAnswering,
                $"ai-raccoon: no server answered at {_client.BaseAddress} within {deadline?.TotalSeconds:0.###}s", ex);
        }

        using (response)
        {
            await EnsureAsync(response, bounded.Token);
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(bounded.Token);
                return await JsonDocument.ParseAsync(stream, cancellationToken: bounded.Token);
            }
            catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException
                                       || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new SettingsServerUnavailableException(ErrorCode.Reach.StoppedAnswering,
                    $"ai-raccoon: the {operation} answer from {_client.BaseAddress} ended before it completed ({ex.Message}); nothing was published", ex);
            }
        }
    }

    private async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new SettingsServerRefusedException(
                $"ai-raccoon: the server at {_client.BaseAddress} refused this credential — it may serve another data root");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new SettingsServerErrorException(
                $"ai-raccoon: the server at {_client.BaseAddress} failed with {(int)response.StatusCode} {response.ReasonPhrase} — this is a server-side fault, not a bad argument");
        }

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await ReadBoundedAsync(response.Content, cancellationToken);
        var (kind, detail) = Problem(body);
        throw new DecisionRefusedException(kind ?? $"http-{(int)response.StatusCode}", (int)response.StatusCode,
            TerminalText.Sanitize($"the server refused the decision request ({kind ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)}): {detail ?? body}"));
    }

    private static (string? Kind, string? Detail) Problem(string body)
    {
        try
        {
            using var problem = JsonDocument.Parse(body);
            var root = problem.RootElement;
            return (Text(root, "kind"), Text(root, "detail") ?? Text(root, "title"));
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static string? Text(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var buffer = new char[RefusalBodyMaxLength];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return new string(buffer, 0, read);
    }
}
