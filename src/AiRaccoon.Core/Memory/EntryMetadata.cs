namespace AiRaccoon.Core.Memory;

/// <summary>On-row degradation metadata for one entry (rating pipeline rewire; FR-NM-1 s2; see docs/work/features-native-memory/native-memory.feature).</summary>
public sealed record EntryMetadata(double Rating, int? TtlDays);
