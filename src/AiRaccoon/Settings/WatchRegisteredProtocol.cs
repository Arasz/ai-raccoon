namespace AiRaccoon.Settings;

/// <summary>
///     The wire contract of the control-plane watch-registered resource (ADR-0075 amendment), shared
///     by the endpoint that serves it and the store that calls it so the two halves cannot drift.
/// </summary>
internal static class WatchRegisteredProtocol
{
    public const string Path = "/watch/registered";
}
