using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Decisions;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Decisions;

/// <summary>A decision the import will create, with the import row that records its source.</summary>
public sealed record PlannedDecision(string DecisionId, string TaskId, DecisionContent Content, DecisionStatus Status, DecisionImport Import);

/// <summary>A graph edge the import will create. Contradicts edges are already in canonical order.</summary>
public sealed record PlannedEdge(string SourceId, string TargetId, DecisionEdgeKind Kind, string Reason, string TaskId);

/// <summary>
///     What importing one Semantica export under one mapping would write, and what blocks it. RetainedImports are the new
///     rows without a decision, including the rows whose relationship became an edge.
/// </summary>
public sealed record SemanticaImportPlan(IReadOnlyList<PlannedDecision> Decisions, IReadOnlyList<PlannedEdge> Edges,
    IReadOnlyList<DecisionImport> RetainedImports, IReadOnlyList<DecisionImportIssue> Blockers, IReadOnlyList<DecisionImportIssue> Notices,
    int EntityCount, int RelationshipCount, int AlreadyImported, int Collapsed)
{
    public bool CanApply => Blockers.Count == 0;

    public bool HasNewRows => Decisions.Count > 0 || RetainedImports.Count > 0;

    public DecisionImportCounts Counts => new(EntityCount, RelationshipCount, Decisions.Count, Edges.Count,
        RetainedImports.Count - Edges.Count, AlreadyImported, Collapsed);

    /// <summary>Uppercase hex SHA-256 of the bytes.</summary>
    public static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>The preview report, with each issue list capped at <see cref="DecisionImportLimits.MaxIssues" />.</summary>
    public DecisionImportReport ToReport(string sourceDigest, string mappingDigest) => new(sourceDigest, mappingDigest, CanApply, Counts,
        [.. Blockers.Take(DecisionImportLimits.MaxIssues)], Math.Max(0, Blockers.Count - DecisionImportLimits.MaxIssues),
        [.. Notices.Take(DecisionImportLimits.MaxIssues)], Math.Max(0, Notices.Count - DecisionImportLimits.MaxIssues));

    /// <summary>
    ///     Plans the import from parsed files, the project's registered tasks, existing import rows and edges, and a native
    ///     decision id for each decision entity. Pure: it reads nothing else and writes nothing.
    /// </summary>
    public static SemanticaImportPlan Build(SemanticaExport export, SemanticaMapping mapping, IReadOnlyCollection<DecisionTask> tasks,
        IReadOnlyList<DecisionImport> imports, IReadOnlyList<DecisionEdge> edges, IReadOnlyDictionary<string, string> decisionIds)
    {
        Guard.IsNotNull(export);
        Guard.IsNotNull(mapping);
        Guard.IsNotNull(tasks);
        Guard.IsNotNull(imports);
        Guard.IsNotNull(edges);
        Guard.IsNotNull(decisionIds);
        return new Planner(export, mapping, tasks, imports, edges, decisionIds).Run();
    }

    private sealed record Source(bool IsEntity, int Index, string Id, string Type, string Raw, JsonElement Element, string? SourceId = null, string? TargetId = null)
    {
        public string Path => IsEntity ? $"entities[{Index}]" : $"relationships[{Index}]";
    }

    private sealed record Resolved(string ExternalId, string TaskId, string? Status, string? Title);

    private sealed class Planner(SemanticaExport export, SemanticaMapping mapping, IReadOnlyCollection<DecisionTask> tasks,
        IReadOnlyList<DecisionImport> imports, IReadOnlyList<DecisionEdge> edges, IReadOnlyDictionary<string, string> decisionIds)
    {
        private readonly List<DecisionImportIssue> _blockers = [];
        private readonly List<DecisionImportIssue> _notices = [];
        private readonly List<PlannedDecision> _decisions = [];
        private readonly List<PlannedEdge> _edges = [];
        private readonly List<DecisionImport> _rows = [];
        private readonly HashSet<string> _taskIds = tasks.Select(task => task.TaskId).ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, DecisionImport> _existing = imports
            .Where(import => import.SourceNamespace == mapping.SourceNamespace)
            .ToDictionary(import => import.ExternalId, StringComparer.Ordinal);
        private readonly Dictionary<string, DecisionEdgeKind> _kinds = new(StringComparer.Ordinal);
        private readonly List<DecisionLink> _links = [.. edges.Where(edge => edge.RemovedRevision is null)
            .Select(edge => new DecisionLink(edge.SourceId, edge.TargetId, edge.Kind))];
        private int _alreadyImported;
        private int _collapsed;

        public SemanticaImportPlan Run()
        {
            CheckMappingHeader();
            var sources = UniqueSources();
            foreach (var key in mapping.Records.Keys.Where(key => sources.All(source => source.Id != key)))
            {
                Block("unknown-record", $"mapping.records.{key}", "The mapping lists a record the export does not contain.", key);
            }

            var resolved = ResolveAll(sources);
            var nativeIds = NativeIds(sources, resolved);
            var decisionEntities = sources.Where(source => source.IsEntity && source.Type == "decision").Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                if (!resolved.TryGetValue(source.Id, out var record))
                {
                    continue;
                }

                if (_existing.TryGetValue(record.ExternalId, out var existing))
                {
                    CompareExisting(source, record, existing);
                    continue;
                }

                if (source.IsEntity)
                {
                    PlanEntity(source, record, nativeIds, source.Type == "decision");
                }
                else
                {
                    PlanRelationship(source, record, nativeIds, decisionEntities);
                }
            }

            return new SemanticaImportPlan(_decisions, _edges, _rows, _blockers, _notices, export.Entities.Count, export.Relationships.Count, _alreadyImported, _collapsed);
        }

        private void CheckMappingHeader()
        {
            if (!IsIdentifier(mapping.SourceNamespace))
            {
                Block("invalid-identifier", "mapping.sourceNamespace", "The source namespace is not a usable identifier.");
            }

            if (!IsIdentifier(mapping.LegacyTaskId))
            {
                Block("invalid-identifier", "mapping.legacyTaskId", "The legacy task is not a usable identifier.");
            }
            else if (!_taskIds.Contains(mapping.LegacyTaskId))
            {
                Block("unknown-task", "mapping.legacyTaskId", $"Task '{mapping.LegacyTaskId}' is not registered; run 'decisions task register' first.");
            }

            foreach (var (type, name) in mapping.EdgeKinds)
            {
                switch (name)
                {
                    case "depends_on":
                        _kinds[type] = DecisionEdgeKind.DependsOn;
                        break;
                    case "supports":
                        _kinds[type] = DecisionEdgeKind.Supports;
                        break;
                    case "contradicts":
                        _kinds[type] = DecisionEdgeKind.Contradicts;
                        break;
                    default:
                        Block("invalid-edge-kind", $"mapping.edgeKinds.{type}", $"'{name}' is not depends_on, supports or contradicts. Import supersession with supersede_decision.");
                        break;
                }
            }
        }

        private List<Source> UniqueSources()
        {
            var all = export.Entities.Select(entity => new Source(true, entity.Index, entity.Id, entity.Type, entity.RawText, entity.Element))
                .Concat(export.Relationships.Select(link => new Source(false, link.Index, link.Id, link.Type, link.RawText, link.Element, link.SourceId, link.TargetId)));
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var unique = new List<Source>();
            foreach (var source in all)
            {
                if (!seen.TryGetValue(source.Id, out var raw))
                {
                    seen[source.Id] = source.Raw;
                    unique.Add(source);
                }
                else if (raw == source.Raw)
                {
                    _collapsed++;
                    Notice("collapsed-duplicate", source.Path, "A byte-equal duplicate of an earlier record was collapsed into one row.", source.Id);
                }
                else
                {
                    Block("duplicate-record", source.Path, "Another record has this ID with different content.", source.Id);
                }
            }

            return unique;
        }

        private Dictionary<string, Resolved> ResolveAll(List<Source> sources)
        {
            var result = new Dictionary<string, Resolved>(StringComparer.Ordinal);
            var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                if (!mapping.Records.TryGetValue(source.Id, out var entry))
                {
                    Block("unmapped-record", source.Path, "The mapping has no entry for this record.", source.Id);
                    continue;
                }

                if (entry.ExternalId is null || !IsIdentifier(entry.ExternalId))
                {
                    Block("invalid-identifier", source.Path, "The externalId is missing or not a usable identifier.", source.Id);
                    continue;
                }

                if (claimed.TryGetValue(entry.ExternalId, out var other))
                {
                    Block("duplicate-external-id", source.Path, $"The externalId is already used by record '{other}'.", source.Id);
                    continue;
                }

                claimed[entry.ExternalId] = source.Id;
                var task = entry.TaskId ?? mapping.LegacyTaskId;
                var ok = true;
                if (entry.TaskId is not null && !IsIdentifier(entry.TaskId))
                {
                    Block("invalid-identifier", source.Path, "The taskId is not a usable identifier.", source.Id);
                    ok = false;
                }
                else if (entry.TaskId is not null && !_taskIds.Contains(entry.TaskId))
                {
                    Block("unknown-task", source.Path, $"Task '{entry.TaskId}' is not registered; run 'decisions task register' first.", source.Id);
                    ok = false;
                }

                var isDecision = source.IsEntity && source.Type == "decision";
                string? status = null;
                if (isDecision)
                {
                    ok &= CheckStatus(source, entry, out status);
                }
                else if (entry.Status is not null)
                {
                    Block("status-on-non-decision", source.Path, "Only decision entities take a status.", source.Id);
                    ok = false;
                }

                if (!isDecision && entry.Title is not null)
                {
                    Block("invalid-content", source.Path, "A title override applies to decision entities only.", source.Id);
                    ok = false;
                }

                if (ok)
                {
                    result[source.Id] = new Resolved(entry.ExternalId, task, status, entry.Title);
                }
            }

            return result;
        }

        private bool CheckStatus(Source source, SemanticaMappingRecord entry, out string? status)
        {
            status = null;
            if (entry.Status is null)
            {
                Block("missing-status", source.Path, "Choose a status for this decision; it is never inferred from the outcome.", source.Id);
                return false;
            }

            var name = entry.Status.ToLowerInvariant();
            if (name == "superseded")
            {
                Block("superseded-status", source.Path, "Import both records as accepted, then call supersede_decision.", source.Id);
                return false;
            }

            if (name is not ("proposed" or "accepted" or "rejected" or "deprecated"))
            {
                Block("unknown-status", source.Path, $"'{entry.Status}' is not proposed, accepted, rejected or deprecated.", source.Id);
                return false;
            }

            status = name;
            return true;
        }

        private Dictionary<string, string> NativeIds(List<Source> sources, Dictionary<string, Resolved> resolved)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in sources.Where(source => source.IsEntity && source.Type == "decision"))
            {
                if (resolved.TryGetValue(source.Id, out var record) && _existing.TryGetValue(record.ExternalId, out var row) && row.DecisionId is not null)
                {
                    result[source.Id] = row.DecisionId;
                }
                else if (decisionIds.TryGetValue(source.Id, out var minted))
                {
                    result[source.Id] = minted;
                }
            }

            return result;
        }

        private void CompareExisting(Source source, Resolved record, DecisionImport existing)
        {
            var mappingJson = MappingJson(source, record);
            if (existing.SourceDigest != Digest(Encoding.UTF8.GetBytes(source.Raw)))
            {
                Block("changed-source", source.Path, "This record changed since it was imported.", source.Id);
            }
            else if (existing.Mapping != mappingJson)
            {
                Block("changed-mapping", source.Path, "The mapping for this record changed since it was imported.", source.Id);
            }
            else
            {
                _alreadyImported++;
                Notice("already-imported", source.Path, "This record is already imported and was skipped.", source.Id);
            }
        }

        private void PlanEntity(Source source, Resolved record, Dictionary<string, string> nativeIds, bool isDecision)
        {
            if (!isDecision)
            {
                Notice("unsupported-entity", source.Path, $"Entity type '{source.Type}' is retained verbatim, not promoted.", source.Id);
                _rows.Add(Row(source, record, null, null));
                return;
            }

            var content = Content(source, record.Title);
            if (content is null)
            {
                return;
            }

            var status = Enum.Parse<DecisionStatus>(record.Status!, ignoreCase: true);
            var id = nativeIds[source.Id];
            var row = Row(source, record, id, null);
            _decisions.Add(new PlannedDecision(id, record.TaskId, content, status, row));
        }

        private void PlanRelationship(Source source, Resolved record, Dictionary<string, string> nativeIds, HashSet<string> decisionEntities)
        {
            if (!_kinds.TryGetValue(source.Type, out var kind))
            {
                Notice("unsupported-relationship", source.Path, $"Relationship type '{source.Type}' is retained verbatim, not promoted.", source.Id);
                if (!EndpointInFile(source.SourceId!) || !EndpointInFile(source.TargetId!))
                {
                    Notice("unresolved-endpoint", source.Path, "An endpoint of this retained relationship is not in the export.", source.Id);
                }

                _rows.Add(Row(source, record, null, null));
                return;
            }

            var from = Resolve(source.SourceId!, nativeIds, decisionEntities);
            var to = Resolve(source.TargetId!, nativeIds, decisionEntities);
            if (from is null || to is null)
            {
                Block("unresolved-endpoint", source.Path, "Both endpoints must be decisions in this export or already imported.", source.Id);
                return;
            }

            if (kind == DecisionEdgeKind.Contradicts && StringComparer.Ordinal.Compare(from, to) > 0)
            {
                (from, to) = (to, from);
            }

            var row = Row(source, record, null, kind);
            _rows.Add(row);
            if (_links.Any(link => link.SourceId == from && link.TargetId == to && link.Kind == kind))
            {
                _collapsed++;
                Notice("collapsed-edge", source.Path, "The edge already exists, so only the import row is written.", source.Id);
                return;
            }

            try
            {
                DecisionGraphRules.EnsureCanLink(from, to, kind, _links);
            }
            catch (InvalidOperationException exception)
            {
                Block("invalid-link", source.Path, exception.Message, source.Id);
                return;
            }

            _links.Add(new DecisionLink(from, to, kind));
            _edges.Add(new PlannedEdge(from, to, kind, $"Imported from Semantica {mapping.SourceNamespace}/{record.ExternalId} ({source.Type}).", record.TaskId));
        }

        private bool EndpointInFile(string id) => export.Entities.Any(entity => entity.Id == id);

        private string? Resolve(string endpointId, Dictionary<string, string> nativeIds, HashSet<string> decisionEntities)
        {
            if (decisionEntities.Contains(endpointId))
            {
                return nativeIds.GetValueOrDefault(endpointId);
            }

            if (export.Entities.Any(entity => entity.Id == endpointId))
            {
                return null;
            }

            var external = mapping.Records.TryGetValue(endpointId, out var entry) && entry.ExternalId is { } named ? named : endpointId;
            return _existing.TryGetValue(external, out var row) ? row.DecisionId : null;
        }

        private DecisionImport Row(Source source, Resolved record, string? decisionId, DecisionEdgeKind? edgeKind) =>
            new(mapping.SourceNamespace, record.ExternalId, decisionId, record.TaskId, Digest(Encoding.UTF8.GetBytes(source.Raw)), source.Raw,
                MappingJson(source, record, edgeKind));

        private string MappingJson(Source source, Resolved record) =>
            MappingJson(source, record, !source.IsEntity && _kinds.TryGetValue(source.Type, out var kind) ? kind : null);

        private static string MappingJson(Source source, Resolved record, DecisionEdgeKind? edgeKind)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("originalId", source.Id);
                writer.WriteString("recordKind", source.IsEntity ? "entity" : "relationship");
                writer.WriteString("type", source.Type);
                writer.WriteString("externalId", record.ExternalId);
                writer.WriteString("taskId", record.TaskId);
                if (record.Status is not null)
                {
                    writer.WriteString("status", record.Status);
                }

                if (record.Title is not null)
                {
                    writer.WriteString("title", record.Title);
                }

                if (edgeKind is { } kind)
                {
                    writer.WriteString("edgeKind", kind switch
                    {
                        DecisionEdgeKind.DependsOn => "depends_on",
                        DecisionEdgeKind.Supports => "supports",
                        _ => "contradicts",
                    });
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private DecisionContent? Content(Source source, string? titleOverride)
        {
            var element = source.Element;
            if (!element.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            {
                Block("invalid-content", $"{source.Path}.properties", "A decision entity needs a properties object.", source.Id);
                return null;
            }

            var title = titleOverride;
            if (title is null && !Text(element, "text", $"{source.Path}.text", source, out title))
            {
                return null;
            }

            if (!Text(properties, "category", $"{source.Path}.properties.category", source, out var category)
                || !Text(properties, "scenario", $"{source.Path}.properties.scenario", source, out var scenario)
                || !Text(properties, "outcome", $"{source.Path}.properties.outcome", source, out var outcome)
                || !Text(properties, "reasoning", $"{source.Path}.properties.reasoning", source, out var reasoning))
            {
                return null;
            }

            double? confidence = null;
            if (properties.TryGetProperty("confidence", out var number) && number.ValueKind != JsonValueKind.Null)
            {
                if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var value))
                {
                    Block("invalid-content", $"{source.Path}.properties.confidence", "Confidence must be a JSON number.", source.Id);
                    return null;
                }

                confidence = value;
            }

            string? maker = null;
            if (properties.TryGetProperty("decision_maker", out var who) && who.ValueKind != JsonValueKind.Null
                && !Text(properties, "decision_maker", $"{source.Path}.properties.decision_maker", source, out maker))
            {
                return null;
            }

            try
            {
                return new DecisionContent(title!, category!, scenario!, outcome!, reasoning!, confidence, maker);
            }
            catch (ArgumentException exception)
            {
                Block("invalid-content", source.Path, exception.Message, source.Id);
                return null;
            }
        }

        private bool Text(JsonElement owner, string name, string path, Source source, out string? value)
        {
            value = null;
            if (!owner.TryGetProperty(name, out var property))
            {
                Block("invalid-content", path, "This field is required.", source.Id);
                return false;
            }

            value = SemanticaJson.ReadString(property);
            if (value is null)
            {
                Block("invalid-content", path, "This field must be a string.", source.Id);
                return false;
            }

            return true;
        }

        private static bool IsIdentifier(string value)
        {
            try
            {
                DecisionText.Identifier(value, nameof(value));
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private void Block(string code, string path, string message, string? recordId = null) => _blockers.Add(new DecisionImportIssue(code, path, message, recordId));

        private void Notice(string code, string path, string message, string? recordId = null) => _notices.Add(new DecisionImportIssue(code, path, message, recordId));
    }
}
