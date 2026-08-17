namespace AiRaccoon.Settings;

/// <summary>
///     The wire contract of the control-plane maintenance-stats resource (ADR-0075 amendment),
///     shared by the endpoint that serves it and the store that calls it so the two halves cannot
///     drift.
/// </summary>
internal static class MaintenanceStatsProtocol
{
    public const string Path = "/maintenance/stats";
}
