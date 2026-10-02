using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>The one <see cref="CommandDefinition" /> factory for bank SQL: no timeout or flags beyond the token and optional transaction.</summary>
public static class Sql
{
    /// <summary>A command carrying <paramref name="parameters" />, the token and, when given, the transaction it must run in.</summary>
    public static CommandDefinition Def(string sql, object? parameters, CancellationToken cancellationToken,
        SqliteTransaction? transaction = null) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    /// <summary>A parameterless command; this overload keeps a token from being bound as the parameters object.</summary>
    public static CommandDefinition Def(string sql, CancellationToken cancellationToken,
        SqliteTransaction? transaction = null) =>
        new(sql, transaction: transaction, cancellationToken: cancellationToken);
}
