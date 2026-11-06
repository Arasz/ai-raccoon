using System.Text.Json;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Decisions;

/// <summary>An operator's entry for one source record. Values are plain strings; the planner judges them.</summary>
public sealed record SemanticaMappingRecord(string? ExternalId, string? TaskId, string? Status, string? Title);

/// <summary>The operator-written mapping from a Semantica export to native tasks, statuses and edge kinds.</summary>
public sealed record SemanticaMapping(string SourceNamespace, string LegacyTaskId,
    IReadOnlyDictionary<string, string> EdgeKinds, IReadOnlyDictionary<string, SemanticaMappingRecord> Records)
{
    private const string Parameter = "mapping";

    /// <summary>Reads a mapping file strictly; unknown fields, wrong types and duplicate keys refuse with an <see cref="ArgumentException" />.</summary>
    public static SemanticaMapping Parse(byte[] mapping)
    {
        Guard.IsNotNull(mapping);
        using var document = SemanticaJson.Parse(mapping, Parameter);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The mapping must be a JSON object.", Parameter);
        }

        RejectUnknown(root, "The mapping", "sourceNamespace", "legacyTaskId", "edgeKinds", "records");
        var edgeKinds = root.TryGetProperty("edgeKinds", out var kinds)
            ? Object(kinds, "edgeKinds").ToDictionary(property => property.Name, property => Required(property.Value, $"edgeKinds.{property.Name}"), StringComparer.Ordinal)
            : [];
        var records = Object(Property(root, "records"), "records")
            .ToDictionary(property => property.Name, property => Record(property.Value, $"records.{property.Name}"), StringComparer.Ordinal);
        return new SemanticaMapping(Required(Property(root, "sourceNamespace"), "sourceNamespace"),
            Required(Property(root, "legacyTaskId"), "legacyTaskId"), edgeKinds, records);
    }

    private static SemanticaMappingRecord Record(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{where} must be a JSON object.", Parameter);
        }

        RejectUnknown(element, where, "externalId", "taskId", "status", "title");
        return new SemanticaMappingRecord(Optional(element, "externalId", where), Optional(element, "taskId", where),
            Optional(element, "status", where), Optional(element, "title", where));
    }

    private static string? Optional(JsonElement element, string name, string where) =>
        element.TryGetProperty(name, out var value) ? Required(value, $"{where}.{name}") : null;

    private static string Required(JsonElement element, string where) =>
        SemanticaJson.ReadString(element) ?? throw new ArgumentException($"{where} must be a string.", Parameter);

    private static JsonElement Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new ArgumentException($"{name} is required.", Parameter);

    private static IEnumerable<JsonProperty> Object(JsonElement element, string where) =>
        element.ValueKind == JsonValueKind.Object ? element.EnumerateObject() : throw new ArgumentException($"{where} must be a JSON object.", Parameter);

    private static void RejectUnknown(JsonElement element, string where, params string[] known)
    {
        var unknown = element.EnumerateObject().Select(property => property.Name).Except(known, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException($"{where} has unknown fields: {string.Join(", ", unknown)}.", Parameter);
        }
    }
}
