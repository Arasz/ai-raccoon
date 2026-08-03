namespace AiRaccoon.Benchmarks.Corpus;

/// <summary>A document in the benchmark corpus.</summary>
public sealed record CorpusDocument(string Id, string Title, string Body, string Source = "")
{
    public string Text => $"{Title}. {Body}";
}

/// <summary>A query with the ids of the documents judged relevant to it.</summary>
public sealed record CorpusQuery(string Id, string Text, IReadOnlyList<string> RelevantDocIds);
