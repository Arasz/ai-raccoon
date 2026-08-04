namespace AiRaccoon.Core.Memory;

/// <summary>Per-modality candidate depth before RRF fusion (plan C Wave 4); the default is the measured sweep optimum (ADR-0006).</summary>
public enum CandidateWindowMode
{
    Max3x100 = 0,
    Max5x50 = 1
}
