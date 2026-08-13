namespace AiRaccoon.Core.Memory.Promotion;

using System.Threading;
using System.Threading.Tasks;

public record PromotionClassResult(
    bool IsEligibleForPromotion,
    float Score,
    string ClassifierName,
    string Reason);

public interface IPromotionClassifier
{
    string Name { get; }

    /// <summary>True when the opt-in ONNX instruct model is active; a disabled classifier still evaluates but on the zero-shot vector path only.</summary>
    bool IsModelEnabled { get; }

    ValueTask<PromotionClassResult> ClassifyCandidateAsync(
        MemoryWriteRequest request,
        CancellationToken cancellationToken = default);
}
