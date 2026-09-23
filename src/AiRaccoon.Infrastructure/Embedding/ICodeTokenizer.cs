namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Counts tokens the way the bundled engine embeds code (ADR-0108) — the
/// chunk budget unit when no other code engine is configured.</summary>
public interface ICodeTokenizer
{
    int CountTokens(string text);
}
