using AiRaccoon.Core.Memory.QueryGuard;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     Combines the tiered read-path guard's verdict (docs/adr/0040) with the always-on query-length
///     verdict (docs/adr/0071) into memory_search's one caller-visible warning string.
/// </summary>
public static class SearchWarnings
{
    public static string? Compose(QueryGuardVerdict guardVerdict, QueryGuardVerdict lengthVerdict)
    {
        List<string>? warnings = null;
        Collect(guardVerdict, ref warnings);
        Collect(lengthVerdict, ref warnings);
        return warnings is null ? null : string.Join(" ", warnings);
    }

    private static void Collect(QueryGuardVerdict verdict, ref List<string>? warnings)
    {
        if (verdict.Tier != QueryGuardTier.Warn)
        {
            return;
        }

        (warnings ??= []).Add(verdict.Guidance!);
    }
}
