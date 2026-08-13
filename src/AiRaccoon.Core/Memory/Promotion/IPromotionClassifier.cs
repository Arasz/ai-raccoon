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
    ValueTask<PromotionClassResult> ClassifyCandidateAsync(
        MemoryWriteRequest request,
        CancellationToken cancellationToken = default);
}
