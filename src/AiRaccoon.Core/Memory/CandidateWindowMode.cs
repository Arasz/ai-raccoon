namespace AiRaccoon.Core.Memory;

/// <summary>Per-modality candidate depth before RRF fusion; the default is the measured sweep optimum (see docs/adr/0006-rrf-parameter-optimization.md).</summary>
public enum CandidateWindowMode
{
    Max3x100 = 0,
    Max5x50 = 1
}
