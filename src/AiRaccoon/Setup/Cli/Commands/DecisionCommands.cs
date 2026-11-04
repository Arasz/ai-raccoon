using System.Buffers;
using System.CommandLine;
using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Decisions;
using AiRaccoon.Decisions.Client;
using AiRaccoon.Hosting.Common;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `decisions task register | export | preview` over the server's decision routes. Machine output
///     goes to stdout, diagnostics to stderr; an export is printed or published only once it is complete.
/// </summary>
internal sealed class DecisionCommands
{
    private static readonly JsonWriterOptions JsonOutput = new() { Indented = true };

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
        var sessionId = parseResult.GetValue<string?>("--session-id");
        var requestId = parseResult.GetValue<string?>("--request-id");
        if (sessionId is null || requestId is null)
        {
            sessionId ??= "cli-" + Guid.CreateVersion7().ToString("N");
            requestId ??= Guid.CreateVersion7().ToString("N");
            await streams.WriteErrorLineAsync($"ai-raccoon: sending with --session-id {sessionId} --request-id {requestId}; pass both to retry this registration safely");
        }

        var request = new RegisterTaskRequest(parseResult.GetValue<string>("--task-id")!, sessionId, requestId,
            parseResult.GetValue<string>("--title")!, parseResult.GetValue<string?>("--source-ref"));
        using var response = await _client.RegisterTaskAsync(ProjectId(parseResult), request, cancellationToken);
        await WriteJsonAsync(streams.Output, json => Data(response).WriteTo(json), cancellationToken);
        return ErrorCode.Ok.Success;
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

    /// <summary>Renders into an owner-only file beside <paramref name="destination" /> and renames it into place only once complete.</summary>
    private static async Task PublishAsync(string destination, bool force, Func<TextWriter, Task> render, CancellationToken cancellationToken)
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
