namespace AiRaccoon.Settings;

/// <summary>
///     The wire contract of the control-plane repair resource (ADR-0075 amendment), shared by the
///     endpoint that serves it and the store that calls it so the two halves cannot drift.
/// </summary>
internal static class RepairProtocol
{
    public const string Path = "/repair";

    public static string ForKind(string kind) => $"{Path}?kind={Uri.EscapeDataString(kind)}";
}

/// <summary>A `repair &lt;verb&gt; --apply` request. The endpoint refuses a kind it does not recognise. <see cref="RepairRequest.MapJson" /> carries the one-shot project-ids alias map (ADR-0099) — null for every other kind.</summary>
internal sealed record RepairRequest(string Kind, string? MapJson = null);
