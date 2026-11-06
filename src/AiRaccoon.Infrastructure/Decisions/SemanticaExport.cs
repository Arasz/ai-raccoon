using System.Text.Json;
using AiRaccoon.Core.Decisions;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Decisions;

/// <summary>One entity of a Semantica export; Element is a detached copy and RawText its exact source text.</summary>
public sealed record SemanticaEntity(int Index, string Id, string Type, string RawText, JsonElement Element);

/// <summary>One relationship of a Semantica export; Element is a detached copy and RawText its exact source text.</summary>
public sealed record SemanticaRelationship(int Index, string Id, string SourceId, string TargetId, string Type, string RawText, JsonElement Element);

/// <summary>The entities and relationships of a Semantica 0.7.0 export_graph JSON file.</summary>
public sealed record SemanticaExport(IReadOnlyList<SemanticaEntity> Entities, IReadOnlyList<SemanticaRelationship> Relationships)
{
    private const string Parameter = "source";

    /// <summary>Reads the export_graph envelope strictly; any deviation refuses the whole file with an <see cref="ArgumentException" />.</summary>
    public static SemanticaExport Parse(byte[] source)
    {
        Guard.IsNotNull(source);
        using var envelope = SemanticaJson.Parse(source, Parameter);
        SemanticaJson.RequireExactKeys(envelope.RootElement, "The envelope", Parameter, "format", "data");
        if (SemanticaJson.ReadString(envelope.RootElement.GetProperty("format")) != "json")
        {
            throw new ArgumentException("Only the export_graph format \"json\" is supported.", Parameter);
        }

        var data = SemanticaJson.ReadString(envelope.RootElement.GetProperty("data"))
            ?? throw new ArgumentException("The envelope data must be a JSON string holding the graph.", Parameter);
        using var inner = SemanticaJson.ParseText(data, Parameter);
        var root = inner.RootElement;
        SemanticaJson.RequireExactKeys(root, "The graph data", Parameter, "entities", "relationships", "statistics");
        var entities = ArrayOf(root.GetProperty("entities"), "entities");
        var relationships = ArrayOf(root.GetProperty("relationships"), "relationships");
        var statistics = root.GetProperty("statistics");
        SemanticaJson.RequireExactKeys(statistics, "The statistics", Parameter, "entity_count", "relationship_count");
        if (Count(statistics, "entity_count") != entities.GetArrayLength() || Count(statistics, "relationship_count") != relationships.GetArrayLength())
        {
            throw new ArgumentException("The statistics counts do not match the entities and relationships; the file is truncated or not a 0.7.0 export.", Parameter);
        }

        return new SemanticaExport(
            [.. entities.EnumerateArray().Select((item, index) => Entity(item, index))],
            [.. relationships.EnumerateArray().Select((item, index) => Relationship(item, index))]);
    }

    private static JsonElement ArrayOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Array ? element : throw new ArgumentException($"{name} must be a JSON array.", Parameter);

    private static int Count(JsonElement statistics, string name) =>
        statistics.GetProperty(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var count)
            ? count
            : throw new ArgumentException($"statistics.{name} must be an integer.", Parameter);

    private static SemanticaEntity Entity(JsonElement item, int index)
    {
        var where = $"entities[{index}]";
        return new SemanticaEntity(index, Text(item, "id", where, identifier: true), Text(item, "type", where), item.GetRawText(), item.Clone());
    }

    private static SemanticaRelationship Relationship(JsonElement item, int index)
    {
        var where = $"relationships[{index}]";
        return new SemanticaRelationship(index, Text(item, "id", where, identifier: true), Text(item, "source_id", where, identifier: true),
            Text(item, "target_id", where, identifier: true), Text(item, "type", where), item.GetRawText(), item.Clone());
    }

    private static string Text(JsonElement item, string name, string where, bool identifier = false)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var property))
        {
            throw new ArgumentException($"{where}.{name} is required.", Parameter);
        }

        var value = SemanticaJson.ReadString(property) ?? throw new ArgumentException($"{where}.{name} must be a string.", Parameter);
        if (identifier)
        {
            try
            {
                DecisionText.Identifier(value, name);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"{where}.{name} is not a usable identifier: {exception.Message}", Parameter, exception);
            }
        }

        return value;
    }
}
