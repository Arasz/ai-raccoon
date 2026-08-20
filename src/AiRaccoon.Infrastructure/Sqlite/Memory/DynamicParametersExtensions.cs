using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

public static class DynamicParametersExtensions
{
    extension(DynamicParameters)
    {
        public static DynamicParameters VectorParameters(SearchQuery query, SearchParameters searchParameters, QueryVector queryVector, string? ctx)
        {
            var parameters = new DynamicParameters();
            parameters.Add("ctx", ctx);
            parameters.Add("limit", searchParameters.CandidateWindowFor(query.Limit));
            parameters.Add("queryVector", queryVector.Data);
            return parameters;
        }

        public static DynamicParameters SearchParameters(SearchQuery query, SearchParameters searchParameters, FtsQueryPlan queryPlan, QueryVector queryVector, ContextFilter contextFilter)
        {
            var parameters = new DynamicParameters();
            parameters.Add("query", queryPlan.Expression);
            parameters.Add("limit", searchParameters.CandidateWindowFor(query.Limit));
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

        public static DynamicParameters FallbackSearchParameters(SearchQuery query, SearchParameters searchParameters, FtsQueryPlan queryPlan, QueryVector queryVector, ContextFilter contextFilter)
        {
            var parameters = new DynamicParameters();
            parameters.Add("query", queryPlan.Fallback);
            parameters.Add("limit", searchParameters.CandidateWindowFor(query.Limit));
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
