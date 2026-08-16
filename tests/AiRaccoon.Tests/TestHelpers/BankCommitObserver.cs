using AiRaccoon.Infrastructure.Sqlite;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     WP7-T1's seam (docs/plans/2026-08-16-bank-open-cost-implementation.md §6): whether anything
///     committed a write transaction to the bank while this was open.
///     <para />
///     <c>PRAGMA data_version</c> is constant on the connection that reads it and changes whenever
///     a *different* connection commits — so one connection held open across a CLI invocation
///     answers "did that process write" for INSERT, UPDATE, DELETE and DDL alike, without
///     enumerating tables and without a per-provider update hook (Microsoft.Data.Sqlite exposes
///     none).
///     <para />
///     Precondition: the bank must already exist and be current. Creating a bank legitimately
///     writes it, as does the first open after a schema change, and neither is what this measures.
/// </summary>
public sealed class BankCommitObserver : IAsyncDisposable
{
    private readonly SqliteConnection _observer;
    private long _mark;

    private BankCommitObserver(SqliteConnection observer) => _observer = observer;

    /// <summary>Opens the observing connection. The bank must already exist.</summary>
    public static async Task<BankCommitObserver> OpenAsync(ISqliteConnectionFactory factory, CancellationToken cancellationToken)
    {
        Guard.IsNotNull(factory);
        var observer = new BankCommitObserver(await factory.OpenBankAsync(cancellationToken));
        await observer.MarkAsync(cancellationToken);
        return observer;
    }

    /// <summary>Takes the reference point that <see cref="HasCommittedSinceMarkAsync" /> compares against.</summary>
    public async Task MarkAsync(CancellationToken cancellationToken) => _mark = await ReadAsync(cancellationToken);

    /// <summary>True when some other connection committed a write transaction since the last mark.</summary>
    public async Task<bool> HasCommittedSinceMarkAsync(CancellationToken cancellationToken) =>
        await ReadAsync(cancellationToken) != _mark;

    public async ValueTask DisposeAsync() => await _observer.DisposeAsync();

    private Task<long> ReadAsync(CancellationToken cancellationToken) =>
        _observer.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA data_version", cancellationToken: cancellationToken));
}
