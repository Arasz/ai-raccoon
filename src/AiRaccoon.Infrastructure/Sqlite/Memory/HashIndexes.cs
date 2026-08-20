namespace AiRaccoon.Infrastructure.Sqlite.Memory;

public sealed record HashIndexes(Dictionary<string, string> ValueByHash, Dictionary<string, string> FtsQueryByHash, Dictionary<string, long> IdByHash)
{
    public static HashIndexes Create()
    {
        var valueByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var ftsQueryByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var idByHash = new Dictionary<string, long>(StringComparer.Ordinal);
        return new HashIndexes(valueByHash, ftsQueryByHash, idByHash);
    }
}
