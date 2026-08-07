namespace AiRaccoon.Observability;

/// <summary>Renders the serve observability copy-paste commands for dotnet-counters/dotnet-trace.</summary>
public static class MonitoringCommandRenderer
{
    public static string RenderCounters(int pid) => $"dotnet-counters monitor -p {pid}";

    public static string RenderTrace(int pid) => $"dotnet-trace collect -p {pid} --providers AiRaccoon.MemoryTools";

    public static string RenderPid(int pid) => pid.ToString();
}
