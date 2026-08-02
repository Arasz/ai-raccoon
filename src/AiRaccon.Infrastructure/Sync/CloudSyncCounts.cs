using System.Text.Json;

namespace AiRaccon.Infrastructure.Sync;

/// <summary>Counts parsed from the JSON that cloudsync_network_sync returns ({"send":{...},"receive":{...}}).</summary>
public sealed record CloudSyncCounts(int Sent, int Received)
{
    public static CloudSyncCounts Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new CloudSyncCounts(Count(document, "send"), Count(document, "receive"));
    }

    private static int Count(JsonDocument document, string section)
    {
        if (!document.RootElement.TryGetProperty(section, out var node) ||
            !node.TryGetProperty("count", out var count) ||
            count.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return count.GetInt32();
    }
}
