using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiRaccoon.Core.Decisions;

/// <summary>Reads and writes a decision enum as its snake_case name and refuses numbers and unknown names.</summary>
public sealed class DecisionEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.SnakeCaseLower, false)
    where TEnum : struct, Enum;
