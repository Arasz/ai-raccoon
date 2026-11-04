using System.Globalization;
using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Decisions;

namespace AiRaccoon.Decisions.Client;

/// <summary>
///     The human-readable rendering of a decision export: the same snapshot as the JSON, with full
///     rationale, history and edges. Decision text is escaped so neither Markdown nor a terminal
///     interprets it, and source URIs are shown, never linked.
/// </summary>
internal static class DecisionMarkdown
{
    private const string MarkdownSpecials = "\\`*_[]<>#|~&!";

    public static IEnumerable<string> Render(string projectId, long projectRevision, DecisionExport export)
    {
        yield return $"# Decision export for project {Code(projectId)}";
        yield return string.Empty;
        yield return $"- Project revision: {projectRevision.ToString(CultureInfo.InvariantCulture)}";
        yield return $"- Schema version: {export.SchemaVersion.ToString(CultureInfo.InvariantCulture)}";
        yield return $"- Exported at: {Time(export.ExportedAt)}";
        yield return $"- Selector: {Selector(export.Selector)}";
        yield return $"- Selected tasks: {List(export.SelectedTaskIds)}";
        yield return $"- Counts: {export.Counts.Tasks} tasks, {export.Counts.Decisions} decisions, {export.Counts.Versions} versions, " +
                     $"{export.Counts.Edges} edges, {export.Counts.BoundaryNodes} boundary decisions, {export.Counts.Imports} import records";
        yield return "- This file is plaintext, even when the bank is encrypted.";

        yield return string.Empty;
        yield return "## Tasks";
        foreach (var line in Tasks(export.Tasks))
        {
            yield return line;
        }

        yield return string.Empty;
        yield return "## Decisions";
        foreach (var decision in export.Decisions)
        {
            foreach (var line in Decision(decision))
            {
                yield return line;
            }
        }

        yield return string.Empty;
        yield return "## Edges";
        foreach (var edge in export.Edges)
        {
            yield return Edge(edge);
        }

        yield return string.Empty;
        yield return "## Boundary decisions (outside the selected tasks)";
        foreach (var node in export.BoundaryNodes)
        {
            yield return $"- {Code(node.DecisionId)} [{Name(node.Status)}] {Inline(node.Title)} (version {node.CurrentVersion})";
        }

        yield return string.Empty;
        yield return "## Referenced tasks";
        foreach (var line in Tasks(export.ReferencedTasks))
        {
            yield return line;
        }

        yield return string.Empty;
        yield return "## Import records";
        foreach (var import in export.ImportRecords)
        {
            foreach (var line in Import(import))
            {
                yield return line;
            }
        }
    }

    /// <summary>One decision as `decisions get` returns it: its record and the versions the server sent.</summary>
    public static IEnumerable<string> RenderDecision(string projectId, long projectRevision, DecisionDetails details)
    {
        var record = details.Record;
        var current = details.Versions.FirstOrDefault(version => version.Version == record.CurrentVersion) ?? details.Versions.LastOrDefault();
        yield return $"# Decision {Code(record.DecisionId)} {Inline(current?.Content.Title ?? string.Empty)}";
        yield return string.Empty;
        yield return $"- Project: {Code(projectId)}; revision {projectRevision.ToString(CultureInfo.InvariantCulture)}";
        yield return $"- Origin task: {Code(record.OriginTaskId)}; created {Time(record.CreatedAt)}; current version {record.CurrentVersion}";
        foreach (var line in details.Versions.SelectMany(Version))
        {
            yield return line;
        }
    }

    /// <summary>Text for a Markdown line: terminal controls made visible, Markdown punctuation escaped, list and heading starts defused.</summary>
    internal static string Inline(string text)
    {
        var visible = TerminalText.Visible(text);
        var result = new StringBuilder(visible.Length);
        foreach (var character in visible)
        {
            if (MarkdownSpecials.Contains(character))
            {
                result.Append('\\');
            }

            result.Append(character);
        }

        var escaped = result.ToString();
        var start = escaped.TrimStart();
        return start.Length > 0 && (start[0] is '-' or '+' or '=' || OrderedListStart(start)) ? "\\" + escaped.TrimStart() : escaped;
    }

    private static bool OrderedListStart(string text)
    {
        var digits = text.TakeWhile(char.IsAsciiDigit).Count();
        return digits > 0 && digits < text.Length && text[digits] is '.' or ')';
    }

    private static IEnumerable<string> Tasks(IReadOnlyList<DecisionTask> tasks) =>
        tasks.Select(task =>
            $"- {Code(task.TaskId)} {Inline(task.Title)} (order {task.RegisteredOrder}, registered {Time(task.RegisteredAt)}" +
            (task.SourceRef is null ? ")" : $", source {Code(task.SourceRef)})"));

    private static IEnumerable<string> Decision(ExportedDecision decision)
    {
        var record = decision.Record;
        var current = decision.Versions.FirstOrDefault(version => version.Version == record.CurrentVersion);
        yield return string.Empty;
        yield return $"### {Code(record.DecisionId)} {Inline(current?.Content.Title ?? string.Empty)}";
        yield return string.Empty;
        yield return $"- Origin task: {Code(record.OriginTaskId)}; created {Time(record.CreatedAt)}; current version {record.CurrentVersion}";
        foreach (var touch in decision.Touches)
        {
            yield return $"- Touched by {Code(touch.TaskId)}: {Inline(touch.Reason)}" +
                         (touch.Version is { } version ? $" (version {version})" : string.Empty) +
                         (touch.EdgeId is { } edge ? $" (edge {Code(edge)})" : string.Empty);
        }

        foreach (var version in decision.Versions)
        {
            foreach (var line in Version(version))
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<string> Version(DecisionVersion version)
    {
        var content = version.Content;
        yield return string.Empty;
        yield return $"#### Version {version.Version} [{Name(version.Status)}]";
        yield return string.Empty;
        yield return $"- Title: {Inline(content.Title)}";
        yield return $"- Task: {Code(version.TaskId)}; project revision {version.ProjectRevision}; recorded {Time(version.RecordedAt)} by {Inline(version.Actor)} in session {Code(version.SessionId)}";
        if (version.ChangeReason is { } reason)
        {
            yield return $"- Change reason: {Inline(reason)}";
        }

        yield return $"- Category: {Inline(content.Category)}";
        if (content.Confidence is { } confidence)
        {
            yield return $"- Confidence: {confidence.ToString(CultureInfo.InvariantCulture)}";
        }

        if (content.Maker is { } maker)
        {
            yield return $"- Maker: {Inline(maker)}";
        }

        if (content.ValidFrom is not null || content.ValidUntil is not null)
        {
            yield return $"- Valid: {(content.ValidFrom is { } from ? Time(from) : "always")} to {(content.ValidUntil is { } until ? Time(until) : "open")}";
        }

        foreach (var line in Block("Scenario", content.Scenario)
                     .Concat(Block("Outcome", content.Outcome))
                     .Concat(Block("Reasoning", content.Reasoning))
                     .Concat(Items("Alternatives", content.Alternatives))
                     .Concat(Items("Consequences", content.Consequences))
                     .Concat(Items("Sources (not fetched)", content.Sources.Select(source => $"{Code(source.Uri)} {Inline(source.Description)}"), escaped: true)))
        {
            yield return line;
        }
    }

    private static IEnumerable<string> Block(string label, string text)
    {
        yield return string.Empty;
        yield return $"**{label}:**";
        yield return string.Empty;
        foreach (var line in text.Split('\n'))
        {
            yield return Inline(line.TrimEnd('\r'));
        }
    }

    private static IEnumerable<string> Items(string label, IEnumerable<string> items, bool escaped = false)
    {
        var lines = items.Select(item => "- " + (escaped ? item : Inline(item))).ToArray();
        if (lines.Length == 0)
        {
            yield break;
        }

        yield return string.Empty;
        yield return $"**{label}:**";
        yield return string.Empty;
        foreach (var line in lines)
        {
            yield return line;
        }
    }

    private static string Edge(DecisionEdge edge)
    {
        var line = $"- {Code(edge.EdgeId)}: {Code(edge.SourceId)} --{Name(edge.Kind)}--> {Code(edge.TargetId)} — {Inline(edge.Reason)} " +
                   $"(task {Code(edge.TaskId)}, revision {edge.CreatedRevision}, {Time(edge.CreatedAt)}, by {Inline(edge.Actor)})";
        return edge.RemovedRevision is { } removed
            ? line + $"; removed at revision {removed} by task {Code(edge.RemovalTaskId ?? string.Empty)}: {Inline(edge.RemovalReason ?? string.Empty)}"
            : line;
    }

    private static IEnumerable<string> Import(DecisionImport import)
    {
        yield return string.Empty;
        yield return $"- {Code(import.SourceNamespace)} / {Code(import.ExternalId)} → decision {Code(import.DecisionId ?? "none")}, task {Code(import.TaskId)}, digest {Code(import.SourceDigest)}";
        foreach (var (label, raw) in new[] { ("Mapping", import.Mapping), ("Raw payload", import.RawPayload) })
        {
            var text = TerminalText.Visible(raw);
            var fence = new string('`', Math.Max(3, LongestRun(text, '`') + 1));
            yield return string.Empty;
            yield return $"  {label}:";
            yield return string.Empty;
            yield return "  " + fence;
            yield return "  " + text;
            yield return "  " + fence;
        }
    }

    /// <summary>An identifier as inline code, its delimiter longer than any backtick run inside it.</summary>
    private static string Code(string value)
    {
        var text = TerminalText.Visible(value);
        var fence = new string('`', LongestRun(text, '`') + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : string.Empty;
        return $"{fence}{pad}{text}{pad}{fence}";
    }

    private static int LongestRun(string text, char character)
    {
        int longest = 0, current = 0;
        foreach (var c in text)
        {
            current = c == character ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static string Selector(DecisionTaskSelector selector) =>
        selector.All ? "all tasks"
        : selector.TaskIds is { } ids ? "tasks " + List(ids)
        : $"latest {selector.Last ?? 1} task(s)";

    private static string List(IReadOnlyList<string> ids) => ids.Count == 0 ? "none" : string.Join(", ", ids.Select(Code));

    private static string Name<TEnum>(TEnum value) where TEnum : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
