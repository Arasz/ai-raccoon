namespace AiRaccoon.Core.Degradation;

/// <summary>An entry's TTL after a set, with the rating gate a sweep also checks before deleting it.</summary>
public sealed record TtlResult(string Hash, int? TtlDays, double Rating, double Threshold, bool CanEverExpire);
