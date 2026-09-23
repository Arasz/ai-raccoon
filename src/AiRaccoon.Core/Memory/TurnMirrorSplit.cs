namespace AiRaccoon.Core.Memory;

/// <summary>The value to score (a prose prefix when a transcript starts late) and whether the candidate is a true turn-mirror, as <see cref="TurnMirrorPrefix.Split" /> reports it.</summary>
internal readonly record struct TurnMirrorSplit(string Value, bool IsMirror);
