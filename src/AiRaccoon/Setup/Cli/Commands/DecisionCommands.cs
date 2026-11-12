using System.Buffers;
using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Decisions;
using AiRaccoon.Decisions.Client;
using AiRaccoon.Hosting.Common;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `decisions task register | record | get | query | export | preview | import-semantica` over the server's decision routes. Machine output
///     goes to stdout, diagnostics to stderr. An export is read in full before it is written; stdout is then written as it
///     renders and can hold partial output if writing fails, while --output publishes the file only once it is complete.
/// </summary>
internal sealed class DecisionCommands
{
    private static readonly JsonWriterOptions JsonOutput = new() { Indented = true };

    /// <summary>The write fields the CLI owns; an input file naming them would send a second, conflicting copy.</summary>
    private static readonly string[] WriteFields = ["taskId", "sessionId", "requestId"];

    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    private readonly IDecisionClient _client;

    public DecisionCommands(IDecisionClient client)
    {
        Guard.IsNotNull(client);
        _client = client;
    }

    /// <summary>The selector the flags name, or null for the server's default (the latest task).</summary>
    internal static DecisionTaskSelector? Selector(ParseResult parseResult)
    {
        var tasks = parseResult.GetValue<int?>("--tasks");
        var all = parseResult.GetValue<bool>("--all");
        var taskIds = parseResult.GetValue<string[]>("--task-id") is { Length: > 0 } ids ? ids : null;
        return tasks is null && !all && taskIds is null ? null : new DecisionTaskSelector(tasks, all, taskIds);
    }

    public async Task<int> RegisterTaskAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var (sessionId, requestId) = await RetryIdsAsync(parseResult, streams, "registration");
        var request = new RegisterTaskRequest(parseResult.GetValue<string>("--task-id")!, sessionId, requestId,
            parseResult.GetValue<string>("--title")!, parseResult.GetValue<string?>("--source-ref"));
        return await SendWriteAsync(streams, "registration", sessionId, requestId,
            () => _client.RegisterTaskAsync(ProjectId(parseResult), request, cancellationToken), cancellationToken);
    }

    public async Task<int> RecordAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var input = parseResult.GetValue<string>("--input")!;
        string text;
        try
        {
            text = await File.ReadAllTextAsync(input, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: could not read {TerminalText.Visible(input)} ({TerminalText.Visible(ex.Message)}); nothing was sent");
            return ErrorCode.Environment.IoFailed;
        }

        if (RecordBody(text) is not { } body)
        {
            await streams.WriteErrorLineAsync(
                $"ai-raccoon: {TerminalText.Visible(input)} must hold one JSON object with unique fields and without {string.Join(", ", WriteFields)} — the task comes from --task-id and the retry identity from --session-id/--request-id; nothing was sent");
            return ErrorCode.Usage.InvalidValue;
        }

        var (sessionId, requestId) = await RetryIdsAsync(parseResult, streams, "record");
        body["taskId"] = parseResult.GetValue<string>("--task-id");
        body["sessionId"] = sessionId;
        body["requestId"] = requestId;
        return await SendWriteAsync(streams, "record", sessionId, requestId,
            () => _client.RecordAsync(ProjectId(parseResult), body, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Sends a write and prints its answer. Ctrl-C from the send on may land after the server committed it, so it
    ///     reports the outcome as unknown with the ids to replay, not the shared "changed nothing".
    /// </summary>
    private static async Task<int> SendWriteAsync(StandardStreams streams, string what, string sessionId, string requestId,
        Func<Task<JsonDocument>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send();
            await WriteJsonAsync(streams.Output, json => Data(response).WriteTo(json), cancellationToken);
            return ErrorCode.Ok.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: cancelled after the {what} was sent; {DecisionHttpClient.Replay(sessionId, requestId)}");
            return ErrorCode.Ok.SIGC;
        }
    }

    public async Task<int> GetAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var projectId = ProjectId(parseResult);
        using var response = await _client.GetAsync(projectId, parseResult.GetValue<string>("decision")!, parseResult.GetValue<bool>("--history"), cancellationToken);
        var data = Data(response);
        if (parseResult.GetValue<string>("--format") != "markdown")
        {
            await WriteJsonAsync(streams.Output, json => data.WriteTo(json), cancellationToken);
            return ErrorCode.Ok.Success;
        }

        var details = data.GetProperty("value").Deserialize<DecisionDetails>(JsonSerializerOptions.Web)!;
        foreach (var line in DecisionMarkdown.RenderDecision(data.GetProperty("projectId").GetString()!, data.GetProperty("projectRevision").GetInt64(), details))
        {
            await streams.Output.WriteLineAsync(line);
        }

        return ErrorCode.Ok.Success;
    }

    public async Task<int> QueryAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var query = new DecisionQuery(parseResult.GetValue<string?>("--query"), parseResult.GetValue<string?>("--category"));
        using var response = await _client.QueryAsync(ProjectId(parseResult), query, cancellationToken);
        await WriteJsonAsync(streams.Output, json => Data(response).WriteTo(json), cancellationToken);
        return ErrorCode.Ok.Success;
    }

    /// <summary>The record request in <paramref name="text" />, or null when it is not one object with unique fields the CLI may complete.</summary>
    private static JsonObject? RecordBody(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: StrictJson) is JsonObject body
                   && !body.Select(field => field.Key).Any(name => WriteFields.Contains(name, StringComparer.OrdinalIgnoreCase))
                ? body
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The flags' session and request ids, generating and announcing any that are missing so the caller can retry.</summary>
    private static async Task<(string SessionId, string RequestId)> RetryIdsAsync(ParseResult parseResult, StandardStreams streams, string what)
    {
        var sessionId = parseResult.GetValue<string?>("--session-id");
        var requestId = parseResult.GetValue<string?>("--request-id");
        if (sessionId is null || requestId is null)
        {
            sessionId ??= $"cli-{Guid.CreateVersion7():N}";
            requestId ??= Guid.CreateVersion7().ToString("N");
            await streams.WriteErrorLineAsync($"ai-raccoon: sending with --session-id {sessionId} --request-id {requestId}; pass both to retry this {what} safely");
        }

        return (sessionId, requestId);
    }

    public async Task<int> ExportAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var output = parseResult.GetValue<string?>("--output");
        var force = parseResult.GetValue<bool>("--force");
        var destination = output is null ? null : Path.GetFullPath(output);
        if (destination is not null && File.Exists(destination) && !force)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: {TerminalText.Visible(destination)} already exists; pass --force to replace it");
            return ErrorCode.Usage.InvalidValue;
        }

        using var response = await _client.ExportAsync(ProjectId(parseResult), Selector(parseResult), cancellationToken);
        var data = Data(response);
        Func<TextWriter, Task> render = parseResult.GetValue<string>("--format") == "markdown"
            ? writer => WriteMarkdownAsync(writer, data, cancellationToken)
            : writer => WriteFlatAsync(writer, data, cancellationToken);
        if (destination is null)
        {
            await render(streams.Output);
            return ErrorCode.Ok.Success;
        }

        try
        {
            await PublishAsync(destination, force, render, cancellationToken);
        }
        catch (IOException ex)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: could not publish {TerminalText.Visible(destination)} ({TerminalText.Visible(ex.Message)}); nothing was published");
            return ErrorCode.Environment.IoFailed;
        }
        catch (UnauthorizedAccessException ex)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: could not publish {TerminalText.Visible(destination)} ({TerminalText.Visible(ex.Message)}); nothing was published");
            return ErrorCode.Environment.PermissionDenied;
        }

        await streams.WriteErrorLineAsync($"ai-raccoon: exported to {TerminalText.Visible(destination)} (plaintext)");
        return ErrorCode.Ok.Success;
    }

    public async Task<int> PreviewAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var options = new DecisionPreviewOptions(Selector(parseResult));
        if (parseResult.GetValue<int?>("--max-decisions") is { } maxDecisions)
        {
            options = options with { MaxDecisions = maxDecisions };
        }

        if (parseResult.GetValue<int?>("--max-edges") is { } maxEdges)
        {
            options = options with { MaxEdges = maxEdges };
        }

        if (parseResult.GetValue<int?>("--max-chars") is { } maxChars)
        {
            options = options with { MaxChars = maxChars };
        }

        using var response = await _client.PreviewAsync(ProjectId(parseResult), options, cancellationToken);
        var data = Data(response);
        if (parseResult.GetValue<string>("--format") == "json")
        {
            await WriteFlatAsync(streams.Output, data, cancellationToken);
        }
        else
        {
            await streams.Output.WriteAsync(TerminalText.Sanitize(data.GetProperty("value").GetProperty("text").GetString()!));
        }

        return ErrorCode.Ok.Success;
    }

    public async Task<int> ImportSemanticaAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var input = parseResult.GetValue<string>("--input")!;
        var mappingPath = parseResult.GetValue<string>("--mapping")!;
        var apply = parseResult.GetValue<bool>("--apply");
        var requestId = parseResult.GetValue<string?>("--request-id");
        var sourceDigest = parseResult.GetValue<string?>("--source-digest");
        var mappingDigest = parseResult.GetValue<string?>("--mapping-digest");
        var revision = parseResult.GetValue<long?>("--expected-project-revision");
        var sessionId = parseResult.GetValue<string?>("--session-id");
        var given = new[] { requestId, sessionId, sourceDigest, mappingDigest, revision?.ToString() };
        if (!apply && given.Any(value => value is not null))
        {
            await streams.WriteErrorLineAsync("ai-raccoon: --request-id, --session-id, --source-digest, --mapping-digest and --expected-project-revision belong to --apply; nothing was sent");
            return ErrorCode.Usage.InvalidValue;
        }

        if (apply && (requestId is null || sourceDigest is null || mappingDigest is null || revision is null))
        {
            await streams.WriteErrorLineAsync("ai-raccoon: --apply needs --request-id, --source-digest, --mapping-digest and --expected-project-revision from the preview; nothing was sent");
            return ErrorCode.Usage.InvalidValue;
        }

        byte[] source;
        byte[] mapping;
        try
        {
            foreach (var path in new[] { input, mappingPath })
            {
                if (new FileInfo(path).Length > DecisionImportLimits.MaxBytes)
                {
                    await streams.WriteErrorLineAsync($"ai-raccoon: {TerminalText.Visible(path)} is over the {DecisionImportLimits.MaxBytes}-byte limit; nothing was sent");
                    return ErrorCode.Usage.InvalidValue;
                }
            }

            source = await File.ReadAllBytesAsync(input, cancellationToken);
            mapping = await File.ReadAllBytesAsync(mappingPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: could not read the import files ({TerminalText.Visible(ex.Message)}); nothing was sent");
            return ErrorCode.Environment.IoFailed;
        }

        var projectId = ProjectId(parseResult);
        if (apply)
        {
            var request = new ImportApplyRequest(sessionId ?? requestId!, requestId!, source, mapping, sourceDigest!, mappingDigest!, revision!.Value);
            return await SendWriteAsync(streams, "import", request.SessionId, request.RequestId,
                () => _client.ImportApplyAsync(projectId, request, cancellationToken), cancellationToken);
        }

        using var response = await _client.ImportPreviewAsync(projectId, source, mapping, cancellationToken);
        var data = Data(response);
        await WriteFlatAsync(streams.Output, data, cancellationToken);
        var report = data.GetProperty("value");
        if (!report.GetProperty("canApply").GetBoolean())
        {
            var count = report.GetProperty("blockers").GetArrayLength() + report.GetProperty("omittedBlockers").GetInt32();
            await streams.WriteErrorLineAsync($"ai-raccoon: the import has {count} blocker{(count == 1 ? "" : "s")}; fix the mapping or the project and preview again. Nothing was written");
            return ErrorCode.Usage.RequestRejected;
        }

        await streams.WriteErrorLineAsync("ai-raccoon: to apply exactly this preview, run:");
        await streams.WriteErrorLineAsync(string.Join(' ', "ai-raccoon", "decisions", "import-semantica", "--project-id", Quote(projectId), "--input", Quote(input),
            "--mapping", Quote(mappingPath), "--apply", "--request-id", Guid.CreateVersion7().ToString("N"),
            "--source-digest", report.GetProperty("sourceDigest").GetString()!, "--mapping-digest", report.GetProperty("mappingDigest").GetString()!,
            "--expected-project-revision", data.GetProperty("projectRevision").GetInt64()));
        return ErrorCode.Ok.Success;
    }

    /// <summary>The word as one shell argument: bare when it is plain, single-quoted otherwise.</summary>
    private static string Quote(string value) =>
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '/' or ':' or '-')
            ? value
            : $"'{TerminalText.Visible(value).Replace("'", "'\\''")}'";

    private static string ProjectId(ParseResult parseResult) => parseResult.GetValue<string>("--project-id")!;

    private static JsonElement Data(JsonDocument response) => response.RootElement.GetProperty("data");

    /// <summary>The result's project header followed by its value's own fields, written array item by array item.</summary>
    private static Task WriteFlatAsync(TextWriter output, JsonElement data, CancellationToken cancellationToken) =>
        WriteJsonAsync(output, async (json, drain) =>
        {
            json.WriteStartObject();
            json.WriteString("projectId", data.GetProperty("projectId").GetString());
            json.WriteNumber("projectRevision", data.GetProperty("projectRevision").GetInt64());
            foreach (var property in data.GetProperty("value").EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    property.WriteTo(json);
                    continue;
                }

                json.WriteStartArray(property.Name);
                foreach (var item in property.Value.EnumerateArray())
                {
                    item.WriteTo(json);
                    await drain();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }, cancellationToken);

    private static Task WriteJsonAsync(TextWriter output, Action<Utf8JsonWriter> write, CancellationToken cancellationToken) =>
        WriteJsonAsync(output, (json, _) =>
        {
            write(json);
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>Writes JSON to <paramref name="output" /> in chunks as <paramref name="write" /> drains, so a large export is never one string.</summary>
    private static async Task WriteJsonAsync(TextWriter output, Func<Utf8JsonWriter, Func<Task>, Task> write, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using var json = new Utf8JsonWriter(buffer, JsonOutput);

        async Task Drain()
        {
            cancellationToken.ThrowIfCancellationRequested();
            await json.FlushAsync(cancellationToken);
            if (buffer.WrittenCount > 0)
            {
                await output.WriteAsync(Encoding.UTF8.GetString(buffer.WrittenSpan));
                buffer.ResetWrittenCount();
            }
        }

        await write(json, Drain);
        await Drain();
        await output.WriteLineAsync();
    }

    private static async Task WriteMarkdownAsync(TextWriter output, JsonElement data, CancellationToken cancellationToken)
    {
        var export = data.GetProperty("value").Deserialize<DecisionExport>(JsonSerializerOptions.Web)!;
        foreach (var line in DecisionMarkdown.Render(data.GetProperty("projectId").GetString()!, data.GetProperty("projectRevision").GetInt64(), export))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync(line);
        }
    }

    /// <summary>
    ///     Renders into a file beside <paramref name="destination" /> (owner-only on POSIX; Windows applies the directory's
    ///     inherited permissions) and renames it into place only once complete.
    /// </summary>
    internal static async Task PublishAsync(string destination, bool force, Func<TextWriter, Task> render, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var staged = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.partial");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnlyFile.OwnerFileMode;
        }

        try
        {
            await using (var stream = new FileStream(staged, options))
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await render(writer);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            File.Move(staged, destination, force);
        }
        catch
        {
            File.Delete(staged);
            throw;
        }
    }
}
