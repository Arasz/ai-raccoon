namespace AiRaccoon.Observability;

/// <summary>Renders the serve observability copy-paste commands for dotnet-counters/dotnet-trace.</summary>
public static class MonitoringCommandRenderer
{
    /// <summary>Names every meter the server registers, so the pasted command shows its own instruments.</summary>
    public static string RenderCounters(int pid) => $"dotnet-counters monitor -p {pid} --counters {string.Join(',', OtlpNames.Meters)}";

    public static string RenderTrace(int pid) => $"dotnet-trace collect -p {pid} --providers {string.Join(',', OtlpNames.Sources)}";

    public static string RenderPid(int pid) => pid.ToString();
}
