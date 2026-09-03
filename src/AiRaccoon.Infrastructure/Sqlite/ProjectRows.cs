namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Which <c>entries</c> rows belong to a project (ADR-0046). Its own committed rows and any
///     context-labelled rows inside it; workspace rows are scratch and shared rows are cross-project,
///     so neither belongs. The schema already partitions this way — see
///     <c>uq_entries_committed_bucket</c>.
/// </summary>
internal static class ProjectRows
{
    /// <summary>The scope list, for a bare <c>scope IN …</c>.</summary>
    public const string Scopes = "('project', 'custom')";

    /// <summary>
    ///     <c>{alias}scope IN ('project', 'custom')</c>. Pass the alias with its dot (<c>"e."</c>)
    ///     or empty for an unaliased table.
    /// </summary>
    public static string Scope(string alias = "") => $"{alias}scope IN {Scopes}";

    /// <summary>Full membership test: in-scope and belonging to <c>@projectId</c>.</summary>
    public static string Of(string alias = "") => $"{Scope(alias)} AND {alias}project_id = @projectId";

    /// <summary>
    ///     Committed project-scope rows of one id (<c>{alias}scope = 'project' AND
    ///     {alias}project_id = @param</c>). The single home of the project-scope literal outside
    ///     display ordering (ADR-0046; air-merge repair fold) — hand-rolled copies are flagged by
    ///     <c>ProjectRowsSingleDefinitionTests</c>.
    /// </summary>
    public static string ProjectScope(string alias = "", string param = "projectId") =>
        $"{alias}scope = 'project' AND {alias}project_id = @{param}";

    /// <summary>
    ///     Foldable project rows: <see cref="ProjectScope" /> plus a context label. Repair folds
    ///     move only labelled rows — bulk-ingested NULL-context rows stay byte-identical.
    /// </summary>
    public static string LabeledProjectScope(string alias = "", string param = "projectId") =>
        $"{ProjectScope(alias, param)} AND {alias}context_label IS NOT NULL";

    /// <summary>
    ///     Orders the committed row ahead of a context-labelled sibling of the same hash. A hash is
    ///     not a unique row — identical content written twice under different labels shares one — so
    ///     any single-row update or read of "the entry for this hash" needs a deterministic winner.
    /// </summary>
    public static string CommittedFirst(string alias = "") => $"CASE WHEN {alias}scope = 'project' THEN 0 ELSE 1 END";
}
