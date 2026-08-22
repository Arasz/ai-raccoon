namespace AiRaccoon.Core.EventPump;

/// <summary>
///     One topic's pump configuration (docs/work/2026-08-22-post-delta-3-plan.md WP11-B1).
///     <paramref name="Ceiling" /> fixes the channel's own bound at construction — a number that
///     never changes; <paramref name="Capacity" /> is the starting effective soft cap enforced by
///     <see cref="EventPump{T}.ApplyCapacity" />, which can shrink or grow at runtime without
///     rebuilding the channel.
/// </summary>
public sealed record PumpTopic(int Ceiling, int Capacity, bool Coalesce);
