namespace AiRaccoon.Core.Ingestion;

/// <summary>Which corpus a file routes to (<see cref="IngestDispatcher" />).</summary>
public enum CorpusKind
{
    Memory,
    Code,
    Neither
}
