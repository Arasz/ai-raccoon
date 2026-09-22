using ModelContextProtocol;

namespace AiRaccoon.Tools;

/// <summary>
///     A query-guard refusal whose message echoes the query back to its sender, while
///     <see cref="RedactedMessage" /> carries the policy/length/hash fingerprint the server's own
///     channels (log lines, OTLP spans) record instead of the text (SECURITY.md: no search queries).
/// </summary>
internal sealed class RefusedQueryException(string message, string redactedMessage) : McpException(message)
{
    /// <summary>The same refusal without the query text — the only form logs and spans may carry.</summary>
    public string RedactedMessage { get; } = redactedMessage;
}
