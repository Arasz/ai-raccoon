using FluentValidation;

namespace AiRaccoon.Core.Degradation;

/// <summary>The per-entry TTL range: 1..36500 days, or null to never expire. Zero is rejected — it
/// would mean "old enough at any age", a delayed delete rather than a TTL.</summary>
public static class EntryTtl
{
    public const int MinDays = 1;

    public const int MaxDays = 36_500;

    public static bool IsValid(int? ttlDays) => ttlDays is null or (>= MinDays and <= MaxDays);

    /// <summary>Guard for the TTL argument; throws the validation exception the MCP layer maps to `invalid-params`.</summary>
    public static void EnsureValid(int? ttlDays)
    {
        if (!IsValid(ttlDays))
        {
            throw new ValidationException(
                $"ttlDays must be between {MinDays} and {MaxDays}, or null to never expire.");
        }
    }
}
