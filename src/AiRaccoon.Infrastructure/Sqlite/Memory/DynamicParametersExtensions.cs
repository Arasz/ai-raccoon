using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

public static class DynamicParametersExtensions
{
    extension(DynamicParameters)
    {
        public static DynamicParameters VectorParameters(SearchQuery query, QueryVector queryVector, string? ctx)
        {
            var parameters = new DynamicParameters();
            parameters.Add("ctx", ctx);
            parameters.Add("limit", query.LimitForCandidateWindow);
            parameters.Add("queryVector", queryVector.Data);
            return parameters;
        }

        public static DynamicParameters SearchParameters(SearchQuery query, FtsQueryPlan queryPlan, QueryVector queryVector, ContextFilter contextFilter)
        {
            var parameters = new DynamicParameters();
            parameters.Add("query", queryPlan.Expression);
            parameters.Add("limit", query.LimitForCandidateWindow);
            if (!queryVector.IsEmpty)
            {
                parameters.Add("queryVector", queryVector.Data);
            }

            foreach (var (key, value) in contextFilter.Values)
            {
                parameters.Add(key, value);
            }

            return parameters;
        }

        public static DynamicParameters FallbackSearchParameters(SearchQuery query, FtsQueryPlan queryPlan, QueryVector queryVector, ContextFilter contextFilter)
        {
            var parameters = new DynamicParameters();
            parameters.Add("query", queryPlan.Fallback);
            parameters.Add("limit", query.LimitForCandidateWindow);
            if (!queryVector.IsEmpty)
            {
                parameters.Add("queryVector", queryVector.Data);
            }

            foreach (var (key, value) in contextFilter.Values)
            {
                parameters.Add(key, value);
            }

            return parameters;
        }
    }
}
