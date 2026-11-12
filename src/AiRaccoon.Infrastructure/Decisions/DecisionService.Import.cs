using AiRaccoon.Core.Decisions;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Decisions;

public sealed partial class DecisionService
{
    public Task<DecisionResult<DecisionImportReport>> PreviewSemanticaImportAsync(string projectId, byte[] source, byte[] mapping,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(source);
        Guard.IsNotNull(mapping);
        RequireImportSize(source, mapping);
        var export = SemanticaExport.Parse(source);
        var parsed = SemanticaMapping.Parse(mapping);
        return store.ReadAsync(projectId, async (reader, ct) =>
        {
            var plan = await PlanImportAsync(reader, export, parsed, ct);
            return new DecisionResult<DecisionImportReport>(projectId, await reader.RevisionAsync(ct),
                plan.ToReport(SemanticaImportPlan.Digest(source), SemanticaImportPlan.Digest(mapping)));
        }, cancellationToken);
    }

    public Task<DecisionResult<DecisionImportOutcome>> ApplySemanticaImportAsync(string projectId, DecisionImportCommand command,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(command);
        Guard.IsNotNull(command.Source, nameof(command.Source));
        Guard.IsNotNull(command.Mapping, nameof(command.Mapping));
        RequireImportSize(command.Source, command.Mapping);
        var sourceDigest = SemanticaImportPlan.Digest(command.Source);
        var mappingDigest = SemanticaImportPlan.Digest(command.Mapping);
        if (!string.Equals(command.SourceDigest, sourceDigest, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(command.MappingDigest, mappingDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw Conflict("The source or mapping bytes differ from the previewed files; run the preview again.");
        }

        var export = SemanticaExport.Parse(command.Source);
        var parsed = SemanticaMapping.Parse(command.Mapping);
        if (!DecisionText.IsIdentifier(parsed.LegacyTaskId))
        {
            throw new DecisionException(DecisionErrorKinds.ImportBlocked, "The import has 1 blocker; run the preview.");
        }

        var write = new DecisionWrite(parsed.LegacyTaskId, command.SessionId, command.RequestId, command.Actor);
        return MutateAsync(projectId, write, "import-semantica", new { sourceDigest, mappingDigest, command.ExpectedProjectRevision },
            async (writer, revision, ct) =>
            {
                await RequireRevisionAsync(writer, command.ExpectedProjectRevision, ct);
                var plan = await PlanImportAsync(writer, export, parsed, ct);
                if (!plan.CanApply)
                {
                    throw new DecisionException(DecisionErrorKinds.ImportBlocked,
                        $"The import has {plan.Blockers.Count} blocker{(plan.Blockers.Count == 1 ? "" : "s")}; run the preview.");
                }

                var now = Now;
                foreach (var decision in plan.Decisions)
                {
                    var version = new DecisionVersion(decision.DecisionId, 1, decision.TaskId, revision, decision.Content, decision.Status,
                        now, write.Actor, write.SessionId, null);
                    await writer.AddRecordAsync(new DecisionRecord(decision.DecisionId, decision.TaskId, 1, now), ct);
                    await writer.AddVersionAsync(version, ct);
                    await writer.AddImportAsync(decision.Import, ct);
                }

                foreach (var edge in plan.Edges)
                {
                    await writer.AddEdgeAsync(new DecisionEdge(Guid.CreateVersion7(now).ToString(), edge.SourceId, edge.TargetId, edge.Kind,
                        edge.Reason, edge.TaskId, revision, now, write.Actor, write.SessionId), ct);
                }

                foreach (var row in plan.RetainedImports)
                {
                    await writer.AddImportAsync(row, ct);
                }

                return new Mutation<DecisionImportOutcome>(new DecisionImportOutcome(sourceDigest, plan.Counts), plan.HasNewRows);
            }, cancellationToken, false);
    }

    private static void RequireImportSize(byte[] source, byte[] mapping)
    {
        if (source.Length > DecisionImportLimits.MaxBytes || mapping.Length > DecisionImportLimits.MaxBytes)
        {
            throw new DecisionException(DecisionErrorKinds.ImportTooLarge,
                $"Each import file must be at most {DecisionImportLimits.MaxBytes} bytes; the source is {source.Length} and the mapping is {mapping.Length}.");
        }
    }

    private async Task<SemanticaImportPlan> PlanImportAsync(IDecisionReader reader, SemanticaExport export, SemanticaMapping mapping,
        CancellationToken cancellationToken)
    {
        var tasks = await reader.TasksAsync(cancellationToken);
        var imports = await reader.ImportsAsync(cancellationToken);
        var edges = await reader.EdgesAsync(cancellationToken);
        var now = Now;
        var ids = export.Entities.Where(entity => entity.Type == "decision").Select(entity => entity.Id).Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, _ => Guid.CreateVersion7(now).ToString(), StringComparer.Ordinal);
        return SemanticaImportPlan.Build(export, mapping, tasks, imports, edges, ids);
    }
}
