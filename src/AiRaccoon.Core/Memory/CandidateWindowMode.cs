namespace AiRaccoon.Core.Memory;

/// <summary>Per-modality candidate depth before RRF fusion; the default is the measured sweep optimum (see docs/adr/0006-rrf-parameter-optimization.md).</summary>
public enum CandidateWindowMode
{
    Max3X100 = 0,
    Max5X50 = 1
}
