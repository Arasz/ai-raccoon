using AiRaccoon.Core.Projects;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>The server's <see cref="IProjectDirectory" />: names read from the projects table, registration through <see cref="IProjectRegistry" />.</summary>
public sealed partial class SqliteProjectDirectory(
    ISqliteConnectionFactory factory,
    IProjectRegistry registry,
    ILogger<SqliteProjectDirectory> logger) : IProjectDirectory
{
    /// <inheritdoc />
    public async Task<ProjectRegistration> RegisterAsync(string projectId, string? name, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        var folded = ProjectIdAliasMap.Default.Apply(projectId);
        var id = folded.ProjectId;
        if (folded.Dropped)
        {
            return new ProjectRegistration(id, ProjectRegistrationOutcome.Retired);
        }

        // The nil guid parses like any other, so it needs its own refusal: it names nothing.
        if (Guid.TryParse(id, out var parsed) && parsed == Guid.Empty)
        {
            return new ProjectRegistration(id, ProjectRegistrationOutcome.NotAGuid);
        }

        if (await registry.IsRegisteredAsync(id, cancellationToken))
        {
            if (name is not null)
            {
                await registry.RegisterAsync(id, name, cancellationToken);
            }

            return new ProjectRegistration(id, ProjectRegistrationOutcome.AlreadyRegistered);
        }

        if (!ProjectId.TryCanonicalize(id, out _) && !await registry.HasRowsAsync(id, cancellationToken))
        {
            return new ProjectRegistration(id, ProjectRegistrationOutcome.NotAGuid);
        }

        await registry.RegisterAsync(id, name, cancellationToken);
        Log.Registered(logger, id);
        return new ProjectRegistration(id, ProjectRegistrationOutcome.Registered);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(name);
        await using var connection = await factory.OpenBankSkippingEnsureAsync(cancellationToken);
        await MemorySchema.EnsureCheapAsync(connection, cancellationToken);
        var ids = await connection.QueryAsync<string>(Sql.Def(MemorySql.SelectProjectIdsByName, new { name }, cancellationToken));
        return [.. ids];
    }

    /// <inheritdoc />
    public async Task<ProjectIdCheck> CheckAsync(string projectId, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        var folded = ProjectIdAliasMap.Default.Apply(projectId);
        var id = folded.ProjectId;
        if (folded.Dropped)
        {
            return new ProjectIdCheck(id, ProjectIdStatus.Retired);
        }

        var known = await registry.IsRegisteredAsync(id, cancellationToken) || await registry.HasRowsAsync(id, cancellationToken);
        return new ProjectIdCheck(id, known ? ProjectIdStatus.Known : ProjectIdStatus.Unknown);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 694, Level = LogLevel.Information,
            Message = "ai-raccoon: project '{ProjectId}' registered through the project directory")]
        public static partial void Registered(ILogger logger, string projectId);
    }
}
