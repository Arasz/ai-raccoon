using AiRaccoon.Core.Decisions;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Decisions;

public sealed partial class DecisionService
{
    public Task<DecisionResult<DecisionExport>> ExportAsync(string projectId, DecisionTaskSelector? selector = null,
        CancellationToken cancellationToken = default) => Store.ReadAsync(projectId, async (reader, ct) =>
    {
        var snapshot = await LoadAsync(reader, true, true, ct);
        return new DecisionResult<DecisionExport>(projectId, snapshot.Revision, Export(snapshot, selector ?? new DecisionTaskSelector(), Now));
    }, cancellationToken);

    public Task<DecisionResult<DecisionPreview>> PreviewAsync(string projectId, DecisionPreviewOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = options ?? new DecisionPreviewOptions();
        Guard.IsInRange(request.MaxDecisions, 1, 101, "maxDecisions");
        Guard.IsInRange(request.MaxEdges, 0, 201, "maxEdges");
        Guard.IsInRange(request.MaxChars, 1000, 50001, "maxChars");
        return Store.ReadAsync(projectId, async (reader, ct) =>
        {
            var snapshot = await LoadAsync(reader, true, true, ct);
            var export = Export(snapshot, request.Selector ?? new DecisionTaskSelector(), Now);
            return new DecisionResult<DecisionPreview>(projectId, snapshot.Revision, Compact(projectId, export, request));
        }, cancellationToken);
    }

    public Task<DecisionResult<DecisionQueryResult>> QueryAsync(string projectId, DecisionQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var request = query ?? new DecisionQuery();
        Guard.IsInRange(request.Limit, 1, 101, "limit");
        ValidateSearch(request.Query, request.Category);
        if (request.AfterId is not null)
        {
            DecisionText.Identifier(request.AfterId, "afterId");
        }

        var statuses = request.Statuses?.ToArray();
        if (statuses is not null)
        {
            Guard.IsInRange(statuses.Length, 1, 6, "statuses");
            Guard.IsTrue(statuses.All(status => Enum.IsDefined(status)), "statuses");
        }

        var selector = request.Selector ?? new DecisionTaskSelector(all: true);
        return Store.ReadAsync(projectId, async (reader, ct) =>
        {
            var snapshot = await LoadAsync(reader, !selector.All, false, ct);
            var selected = SelectedIds(snapshot, SelectTasks(snapshot.Tasks, selector));
            var ranks = request.Query is null ? null : (await reader.SearchAsync(request.Query, ct)).ToDictionary(hit => hit.DecisionId, hit => hit.Rank, StringComparer.Ordinal);
            var matches = snapshot.Current.Values.Where(version => selected.Contains(version.DecisionId)
                && (request.Category is null || version.Content.Category == request.Category)
                && (statuses is null || statuses.Contains(version.Status))
                && (ranks is null || ranks.ContainsKey(version.DecisionId))).OrderBy(version => version.DecisionId, StringComparer.Ordinal).ToArray();
            var page = matches.Where(version => request.AfterId is null || StringComparer.Ordinal.Compare(version.DecisionId, request.AfterId) > 0)
                .Take(request.Limit + 1).ToArray();
            var hasMore = page.Length > request.Limit;
            var shown = page.Take(request.Limit).Select(version => Match(version, ranks?.GetValueOrDefault(version.DecisionId))).ToArray();
            return new DecisionResult<DecisionQueryResult>(projectId, snapshot.Revision,
                new DecisionQueryResult(shown, matches.Length, hasMore, hasMore ? shown[^1].Decision.DecisionId : null, ranks is null ? "exact" : "fts"));
        }, cancellationToken);
    }

    public Task<DecisionResult<DecisionQueryResult>> PrecedentsAsync(string projectId, string scenario, string? category = null,
        int limit = 5, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        ValidateSearch(scenario, category);
        DecisionText.Required(scenario, 2000, nameof(scenario));
        Guard.IsInRange(limit, 1, 21, nameof(limit));
        return Store.ReadAsync(projectId, async (reader, ct) =>
        {
            var snapshot = await LoadAsync(reader, false, false, ct);
            var ranks = (await reader.SearchAsync(scenario, ct)).ToDictionary(hit => hit.DecisionId, hit => hit.Rank, StringComparer.Ordinal);
            var now = Now;
            var matches = snapshot.Current.Values.Where(version => ranks.ContainsKey(version.DecisionId)
                && (category is null || version.Content.Category == category)
                && (includeInactive || version.Status == DecisionStatus.Accepted
                    && (!version.Content.ValidFrom.HasValue || version.Content.ValidFrom.Value <= now)
                    && (!version.Content.ValidUntil.HasValue || version.Content.ValidUntil.Value > now)))
                .OrderBy(version => ranks[version.DecisionId]).ThenBy(version => version.DecisionId, StringComparer.Ordinal).ToArray();
            return new DecisionResult<DecisionQueryResult>(projectId, snapshot.Revision,
                new DecisionQueryResult(matches.Take(limit).Select(version => Match(version, ranks[version.DecisionId])).ToArray(), matches.Length,
                    matches.Length > limit, null, "fts"));
        }, cancellationToken);
    }

    public Task<DecisionResult<DecisionGraph>> GraphAsync(string projectId, DecisionGraphRequest request,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(request);
        DecisionText.Identifier(request.DecisionId, "decisionId");
        Guard.IsTrue(Enum.IsDefined(request.Direction), "direction");
        Guard.IsInRange(request.Depth, 0, 11, "depth");
        Guard.IsInRange(request.MaxNodes, 1, 501, "maxNodes");
        return Store.ReadAsync(projectId, async (reader, ct) =>
        {
            var snapshot = await LoadAsync(reader, false, false, ct);
            if (!snapshot.Current.ContainsKey(request.DecisionId))
            {
                throw NotFound("Decision");
            }

            var live = snapshot.Edges.Where(edge => edge.RemovedRevision is null
                && (request.IncludeContext || edge.Kind is DecisionEdgeKind.DependsOn or DecisionEdgeKind.Supersedes)).ToArray();
            var visited = new HashSet<string>(StringComparer.Ordinal) { request.DecisionId };
            var pending = new Queue<(string Id, int Depth)>();
            pending.Enqueue((request.DecisionId, 0));
            var reasons = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryDequeue(out var node))
            {
                foreach (var edge in live)
                {
                    string? next = null;
                    if ((request.Direction is DecisionDirection.Upstream or DecisionDirection.Both || edge.Kind == DecisionEdgeKind.Contradicts) && edge.SourceId == node.Id)
                    {
                        next = edge.TargetId;
                    }
                    else if ((request.Direction is DecisionDirection.Downstream or DecisionDirection.Both || edge.Kind == DecisionEdgeKind.Contradicts) && edge.TargetId == node.Id)
                    {
                        next = edge.SourceId;
                    }

                    if (next is null || visited.Contains(next))
                    {
                        continue;
                    }

                    if (node.Depth >= request.Depth)
                    {
                        reasons.Add("depth");
                    }
                    else if (visited.Count >= request.MaxNodes)
                    {
                        reasons.Add("nodes");
                    }
                    else
                    {
                        visited.Add(next);
                        pending.Enqueue((next, node.Depth + 1));
                    }
                }
            }

            var contained = live.Where(edge => visited.Contains(edge.SourceId) && visited.Contains(edge.TargetId)).ToArray();
            if (contained.Length > 2000)
            {
                reasons.Add("edges");
            }

            var unknown = reasons.Contains("depth") || reasons.Contains("nodes");
            return new DecisionResult<DecisionGraph>(projectId, snapshot.Revision,
                new DecisionGraph(visited.Order(StringComparer.Ordinal).Select(id => Node(snapshot.Current[id])).ToArray(), contained.Take(2000).ToArray(),
                    reasons.Count > 0, reasons.Order(StringComparer.Ordinal).ToArray(), unknown ? null : 0,
                    unknown ? null : Math.Max(0, contained.Length - 2000)));
        }, cancellationToken);
    }

    private sealed record Snapshot(long Revision, IReadOnlyList<DecisionTask> Tasks, IReadOnlyList<DecisionRecord> Records,
        IReadOnlyDictionary<string, DecisionVersion> Current, IReadOnlyList<DecisionVersion> Versions,
        IReadOnlyList<DecisionEdge> Edges, IReadOnlyList<DecisionImport> Imports);

    private static async Task<Snapshot> LoadAsync(IDecisionReader reader, bool history, bool imports, CancellationToken ct)
    {
        var revision = await reader.RevisionAsync(ct);
        var tasks = await reader.TasksAsync(ct);
        var records = await reader.RecordsAsync(ct);
        var current = await reader.CurrentVersionsAsync(ct);
        var versions = history ? await reader.AllVersionsAsync(ct) : current;
        var edges = await reader.EdgesAsync(ct);
        var source = imports ? await reader.ImportsAsync(ct) : [];
        return new Snapshot(revision, tasks, records, current.ToDictionary(version => version.DecisionId, StringComparer.Ordinal), versions, edges, source);
    }

    private static IReadOnlyList<DecisionTask> SelectTasks(IReadOnlyList<DecisionTask> tasks, DecisionTaskSelector selector)
    {
        if (selector.TaskIds is { } ids)
        {
            var known = tasks.Select(task => task.TaskId).ToHashSet(StringComparer.Ordinal);
            if (ids.Any(id => !known.Contains(id)))
            {
                throw NotFound("Selected task");
            }

            var selected = ids.ToHashSet(StringComparer.Ordinal);
            return tasks.Where(task => selected.Contains(task.TaskId)).ToArray();
        }

        return selector.All ? tasks : tasks.Take(selector.Last!.Value).ToArray();
    }

    private static HashSet<string> SelectedIds(Snapshot snapshot, IReadOnlyList<DecisionTask> tasks)
    {
        var selected = tasks.Select(task => task.TaskId).ToHashSet(StringComparer.Ordinal);
        var ids = snapshot.Records.Where(record => selected.Contains(record.OriginTaskId)).Select(record => record.DecisionId).ToHashSet(StringComparer.Ordinal);
        ids.UnionWith(snapshot.Versions.Where(version => selected.Contains(version.TaskId)).Select(version => version.DecisionId));
        ids.UnionWith(snapshot.Edges.Where(edge => selected.Contains(edge.TaskId) || edge.RemovalTaskId is { } removed && selected.Contains(removed)).Select(edge => edge.SourceId));
        return ids;
    }

    private static DecisionExport Export(Snapshot snapshot, DecisionTaskSelector selector, DateTimeOffset now)
    {
        var tasks = SelectTasks(snapshot.Tasks, selector);
        var taskIds = tasks.Select(task => task.TaskId).ToHashSet(StringComparer.Ordinal);
        var selected = SelectedIds(snapshot, tasks);
        var versions = snapshot.Versions.ToLookup(version => version.DecisionId, StringComparer.Ordinal);
        var decisions = snapshot.Records.Where(record => selected.Contains(record.DecisionId)).Select(record =>
        {
            var history = versions[record.DecisionId].ToArray();
            var touches = history.Where(version => taskIds.Contains(version.TaskId)).Select(version => new DecisionTouch(version.TaskId, "version", version.Version)).ToList();
            foreach (var edge in snapshot.Edges.Where(edge => edge.SourceId == record.DecisionId))
            {
                if (taskIds.Contains(edge.TaskId))
                {
                    touches.Add(new DecisionTouch(edge.TaskId, "edge-created", EdgeId: edge.EdgeId));
                }

                if (edge.RemovalTaskId is { } removed && taskIds.Contains(removed))
                {
                    touches.Add(new DecisionTouch(removed, "edge-removed", EdgeId: edge.EdgeId));
                }
            }

            return new ExportedDecision(record, history, touches);
        }).ToArray();
        var edges = snapshot.Edges.Where(edge => selected.Contains(edge.SourceId) || selected.Contains(edge.TargetId)).ToArray();
        var boundaries = edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).Where(id => !selected.Contains(id)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Select(id => Node(snapshot.Current[id], true)).ToArray();
        var source = snapshot.Imports.Where(import => taskIds.Contains(import.TaskId) || import.DecisionId is { } id && selected.Contains(id)).ToArray();
        var referenced = decisions.SelectMany(decision => decision.Versions.Select(version => version.TaskId).Append(decision.Record.OriginTaskId))
            .Concat(edges.Select(edge => edge.TaskId)).Concat(edges.Where(edge => edge.RemovalTaskId is not null).Select(edge => edge.RemovalTaskId!))
            .Concat(source.Select(import => import.TaskId)).ToHashSet(StringComparer.Ordinal);
        referenced.ExceptWith(taskIds);
        return new DecisionExport(1, now, selector, tasks.Select(task => task.TaskId).ToArray(), tasks, decisions, edges, boundaries,
            snapshot.Tasks.Where(task => referenced.Contains(task.TaskId)).ToArray(), source,
            new DecisionExportCounts(tasks.Count, decisions.Length, decisions.Sum(decision => decision.Versions.Count), edges.Length, boundaries.Length, source.Length));
    }

    private static DecisionNode Node(DecisionVersion version, bool boundary = false) => new(version.DecisionId, version.Content.Title, version.Status, version.Version, boundary);
    private static DecisionMatch Match(DecisionVersion version, double? rank) => new(Node(version), version.Content.Category,
        Excerpt(version.Content.Outcome, 240), Excerpt(version.Content.Reasoning, 240), version.Content.Sources, rank);
    private static void ValidateSearch(string? query, string? category)
    {
        if (query is not null)
        {
            DecisionText.Required(query, 2000, nameof(query));
        }

        if (category is not null)
        {
            DecisionText.Required(category, 100, nameof(category));
        }
    }
}
