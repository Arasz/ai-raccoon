using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

public static class SearchQueryExtensions
{
    extension(SearchParameters parameters)
    {
        public int CandidateWindowFor(int limit) =>
            parameters.CandidateWindow == CandidateWindowMode.Max5X50
                ? (int)Math.Clamp((long)limit * 5, 50, int.MaxValue)
                : (int)Math.Clamp((long)limit * 3, 100, int.MaxValue);
        public double SourceLambdaFor(FtsQueryPlan queryPlan) => queryPlan.IsPathQuery ? 0 : parameters.SourceLambda;
        public bool IsFtsQueried(FtsQueryPlan queryPlan) => queryPlan.Expression.Length > 0 && parameters.FtsWeight != 0;
        public bool IsVectorQueried(QueryVector queryVector) => !queryVector.IsEmpty && parameters.VectorWeight != 0;
    }
}
