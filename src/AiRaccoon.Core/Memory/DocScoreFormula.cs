namespace AiRaccoon.Core.Memory;

/// <summary>How a source file's document score is derived from its chunks' boosted scores (plan C Wave 3).</summary>
public enum DocScoreFormula
{
    /// <summary>Document score = the file's best chunk score; the default (document champion).</summary>
    Max,

    /// <summary>Document score = the sum of the file's chunk scores.</summary>
    Sum
}
