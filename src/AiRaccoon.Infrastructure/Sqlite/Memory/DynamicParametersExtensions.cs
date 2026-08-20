using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

public static class DynamicParametersExtensions
{
    extension(DynamicParameters)
    {
        public static DynamicParameters VectorParameters(string? ctx, int limit, byte[]? queryVector)
        {
            var parameters = new DynamicParameters();
            parameters.Add("ctx", ctx);
            parameters.Add("limit", limit);
            parameters.Add("queryVector", queryVector);
            return parameters;
        }

        public static DynamicParameters SearchParameters(string ftsExpression, int limit, byte[]? queryVector, Dictionary<string, object?> values)
        {
            var parameters = new DynamicParameters();
            parameters.Add("query", ftsExpression);
            parameters.Add("limit", limit);
            if (queryVector is not null)
            {
                parameters.Add("queryVector", queryVector);
            }

            foreach (var (key, value) in values)
            {
                parameters.Add(key, value);
            }

            return parameters;
        }
    }
}
