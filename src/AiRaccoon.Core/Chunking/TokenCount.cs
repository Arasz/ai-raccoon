namespace AiRaccoon.Core.Chunking;

/// <summary>Counts tokens in a text span; injected so the splitter stays dependency-free.</summary>
public delegate int TokenCount(string text);
