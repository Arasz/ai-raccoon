using System.Text.Json;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Tools;

namespace AiRaccoon.Decisions;

/// <summary>One decision refusal as both adapters report it: the kind, the HTTP status and the details.</summary>
internal sealed record DecisionRefusal(string Kind, int Status, string Message, IReadOnlyDictionary<string, object?> Details)
{
    /// <summary>The MCP error text: the kind as prefix, the message, then any details.</summary>
    public string Text => Details.Count == 0
        ? $"{Kind}: {Message}"
        : $"{Kind}: {Message} ({string.Join(", ", Details.Select(d => $"{d.Key}={d.Value}"))})";
}

/// <summary>
///     The single mapping from a decision call's exception to its refusal, read by the HTTP routes
///     and by the MCP refusal filter so both adapters refuse the same call the same way.
/// </summary>
internal static class DecisionRefusals
{
    internal const string InvalidArgument = "invalid-argument";

    /// <summary>The largest serialized result an MCP decision tool returns; larger exports go through the CLI.</summary>
    internal const int McpResultLimit = 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

    /// <summary>The refusal for <paramref name="exception" />, or null when it is a genuine failure.</summary>
    internal static DecisionRefusal? For(Exception exception) => exception switch
    {
        DecisionRevisionConflictException conflict => new DecisionRefusal(DecisionErrorKinds.Conflict, StatusCodes.Status409Conflict,
            conflict.Message, new Dictionary<string, object?> { ["currentProjectRevision"] = conflict.CurrentRevision }),
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => new DecisionRefusal(DecisionErrorKinds.ImportTooLarge,
            StatusCodes.Status413PayloadTooLarge, "The request body is larger than the server accepts.", None),
        DecisionException refusal => new DecisionRefusal(refusal.Kind, StatusFor(refusal.Kind), refusal.Message, Details(refusal)),
        ArgumentException or JsonException => new DecisionRefusal(InvalidArgument, StatusCodes.Status400BadRequest, exception.Message, None),
        _ when ToolRefusals.PrefixFor(exception) is { } kind => new DecisionRefusal(kind, StatusCodes.Status422UnprocessableEntity,
            exception.Message + ToolRefusals.RemedyFor(kind), None),
        _ => null
    };

    /// <summary>Refuses a result whose serialized form is over <see cref="McpResultLimit" />, naming the command that streams it.</summary>
    internal static void EnsureWithinMcpLimit<T>(T result, string projectId, JsonSerializerOptions options)
    {
        var size = JsonSerializer.SerializeToUtf8Bytes(result, options).Length;
        if (size > McpResultLimit)
        {
            throw new DecisionException(DecisionErrorKinds.ExportTooLarge,
                $"The export is {size} bytes, over the {McpResultLimit}-byte MCP limit, so nothing was returned. " +
                $"Run 'ai-raccoon decisions export --project-id {projectId}' with the same selector to stream it in full.");
        }
    }

    private static int StatusFor(string kind) => kind switch
    {
        DecisionErrorKinds.NotFound => StatusCodes.Status404NotFound,
        DecisionErrorKinds.ExportTooLarge or DecisionErrorKinds.ImportTooLarge => StatusCodes.Status413PayloadTooLarge,
        _ => StatusCodes.Status409Conflict
    };

    private static Dictionary<string, object?> Details(DecisionException refusal)
    {
        var details = new Dictionary<string, object?>();
        if (refusal.CurrentVersion is { } version)
        {
            details["currentVersion"] = version;
        }

        if (refusal.CurrentProjectRevision is { } revision)
        {
            details["currentProjectRevision"] = revision;
        }

        return details;
    }
}
