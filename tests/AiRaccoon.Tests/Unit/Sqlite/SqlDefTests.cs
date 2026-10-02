using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sqlite;

/// <summary>The shared command factory carries exactly what it is given: sql, parameters, token and transaction, with no timeout or flags of its own.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SqlDefTests
{
    [Fact]
    public void WithParameters_CarriesSqlParametersAndToken()
    {
        using var cts = new CancellationTokenSource();
        var parameters = new { key = "k" };

        var command = Sql.Def("SELECT 1", parameters, cts.Token);

        command.CommandText.ShouldBe("SELECT 1");
        command.Parameters.ShouldBeSameAs(parameters);
        command.CancellationToken.ShouldBe(cts.Token);
        command.Transaction.ShouldBeNull();
        command.CommandTimeout.ShouldBeNull();
    }

    [Fact]
    public void WithoutParameters_TakesTheTokenAsTheToken_NotAsParameters()
    {
        using var cts = new CancellationTokenSource();

        var command = Sql.Def("SELECT 1", cts.Token);

        command.Parameters.ShouldBeNull();
        command.CancellationToken.ShouldBe(cts.Token);
    }

    [Fact]
    public async Task WithTransaction_CarriesTheTransaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var command = Sql.Def("SELECT 1", null, CancellationToken.None, transaction);

        command.Transaction.ShouldBeSameAs(transaction);
    }
}
