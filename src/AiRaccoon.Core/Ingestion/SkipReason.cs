using System.Text.Json.Serialization;

namespace AiRaccoon.Core.Ingestion;

/// <summary>Why an ingest indexed nothing for a file it was asked to index; a skipped file is never fingerprinted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SkipReason>))]
public enum SkipReason
{
    /// <summary>Neither the memory nor the code corpus handles the file type.</summary>
    [JsonStringEnumMemberName("unsupported-type")]
    UnsupportedType,

    /// <summary>The content is binary.</summary>
    [JsonStringEnumMemberName("binary")]
    Binary,

    /// <summary>A memory handler claimed the file but produced no chunks from non-blank content.</summary>
    [JsonStringEnumMemberName("unparseable")]
    Unparseable
}

/// <summary>Renders a <see cref="SkipReason" /> the way JSON does.</summary>
public static class SkipReasonExtensions
{
    public static string ToWireName(this SkipReason reason) => reason switch
    {
        SkipReason.UnsupportedType => "unsupported-type",
        SkipReason.Binary => "binary",
        SkipReason.Unparseable => "unparseable",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };
}
