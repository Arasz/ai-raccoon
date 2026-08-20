using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

public static class SearchQueryExtensions
{
    extension(SearchQuery query)
    {
        public int LimitForCandidateWindow =>
            query.CandidateWindow == CandidateWindowMode.Max5X50
                ? (int)Math.Clamp((long)query.Limit * 5, 50, int.MaxValue)
                : (int)Math.Clamp((long)query.Limit * 3, 100, int.MaxValue);
        public double SourceLambda(FtsQueryPlan queryPlan) => queryPlan.IsPathQuery ? 0 : query.SourceLambda;
        public bool IsFtsQueried(FtsQueryPlan queryPlan) => queryPlan.Expression.Length > 0 && query.FtsWeight != 0;
        public bool IsVectorQueried(QueryVector queryVector) => !queryVector.IsEmpty && query.VectorWeight != 0;
    }
}
