using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Decisions;

namespace AiRaccoon.Infrastructure.Decisions;

public sealed partial class DecisionService
{
    private static DecisionPreview Compact(string projectId, DecisionExport export, DecisionPreviewOptions options)
    {
        var text = new StringBuilder();
        var chars = 0;
        var textBudget = options.MaxChars - RuneCount(projectId);
        var limit = textBudget - 400;
        bool Append(string value, int? ceiling = null)
        {
            var length = RuneCount(value);
            if (chars + length > (ceiling ?? limit))
            {
                return false;
            }

            text.Append(value);
            chars += length;
            return true;
        }

        Append($"Project {Display(projectId)}; selected tasks: {export.Tasks.Count}\n");
        var fullIds = export.Decisions.Select(decision => decision.Record.DecisionId).ToHashSet(StringComparer.Ordinal);
        var live = export.Edges.Where(edge => edge.RemovedRevision is null).ToArray();
        var neededBoundary = live.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).Where(id => !fullIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var boundaries = export.BoundaryNodes.Where(node => neededBoundary.Contains(node.DecisionId)).ToDictionary(node => node.DecisionId, StringComparer.Ordinal);
        var shownTasks = 0;
        foreach (var task in export.Tasks.Take(export.Decisions.Count > 0 ? 3 : export.Tasks.Count))
        {
            if (!Append($"Task {Display(task.TaskId)}: {Display(task.Title)}\n"))
            {
                break;
            }

            shownTasks++;
        }

        var displayed = new HashSet<string>(StringComparer.Ordinal);
        var abbreviated = 0;
        var decisionLimit = live.Length > 0 && options.MaxEdges > 0 ? limit * 2 / 3 : limit;
        foreach (var decision in export.Decisions.Take(options.MaxDecisions))
        {
            var current = decision.Versions.Single(version => version.Version == decision.Record.CurrentVersion);
            var outcome = Excerpt(current.Content.Outcome, 240);
            var reason = Excerpt(current.Content.Reasoning, 240);
            var block = $"{current.DecisionId} [{current.Status.ToString().ToLowerInvariant()}] {Display(current.Content.Title)}\n  Outcome: {outcome}\n  Why: {reason}\n";
            if (!Append(block, decisionLimit))
            {
                break;
            }

            displayed.Add(current.DecisionId);
            abbreviated += (RuneCount(Display(current.Content.Outcome)) > 240 ? 1 : 0)
                + (RuneCount(Display(current.Content.Reasoning)) > 240 ? 1 : 0);
        }

        var shownBoundaries = new HashSet<string>(StringComparer.Ordinal);
        var shownEdges = 0;
        foreach (var edge in live)
        {
            if (shownEdges >= options.MaxEdges)
            {
                break;
            }

            if (fullIds.Contains(edge.SourceId) && !displayed.Contains(edge.SourceId)
                || fullIds.Contains(edge.TargetId) && !displayed.Contains(edge.TargetId))
            {
                continue;
            }

            var newBoundaries = new[] { edge.SourceId, edge.TargetId }.Where(id => !fullIds.Contains(id) && !shownBoundaries.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
            var block = new StringBuilder();
            foreach (var id in newBoundaries)
            {
                var node = boundaries[id];
                block.AppendLine($"{id} [{node.Status.ToString().ToLowerInvariant()}, boundary] {Display(node.Title)}");
            }

            block.AppendLine($"{edge.SourceId} --{JsonNamingPolicy.SnakeCaseLower.ConvertName(edge.Kind.ToString())}--> {edge.TargetId}");
            if (!Append(block.ToString()))
            {
                continue;
            }

            shownBoundaries.UnionWith(newBoundaries);
            shownEdges++;
        }

        var total = new DecisionPreviewCounts(export.Tasks.Count, export.Decisions.Count, boundaries.Count, live.Length);
        var shown = new DecisionPreviewCounts(shownTasks, displayed.Count, shownBoundaries.Count, shownEdges);
        var omitted = new DecisionPreviewCounts(total.Tasks - shown.Tasks, total.Decisions - shown.Decisions,
            total.BoundaryNodes - shown.BoundaryNodes, total.Edges - shown.Edges);
        text.Append($"Shown tasks {shown.Tasks}/{total.Tasks}, decisions {shown.Decisions}/{total.Decisions}, boundary nodes {shown.BoundaryNodes}/{total.BoundaryNodes}, edges {shown.Edges}/{total.Edges}; omitted tasks {omitted.Tasks}, decisions {omitted.Decisions}, boundary nodes {omitted.BoundaryNodes}, edges {omitted.Edges}; abbreviated fields {abbreviated}.\n");
        return new DecisionPreview(text.ToString(), total, shown, omitted, abbreviated);
    }

    private static int RuneCount(string value) => value.EnumerateRunes().Count();

    private static string Display(string value)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            result.Append(Rune.IsControl(rune) ? " " : rune.ToString());
        }

        return result.ToString();
    }

    private static string Excerpt(string value, int maximum)
    {
        var display = Display(value);
        var runes = display.EnumerateRunes().Take(maximum + 1).ToArray();
        return runes.Length <= maximum ? display : string.Concat(runes.Take(maximum - 1).Select(rune => rune.ToString())) + "…";
    }
}
