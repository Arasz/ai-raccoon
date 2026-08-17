namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Counts tokens the way the bundled local embedding engine will (docs/adr/0036).</summary>
public interface ILocalTokenizer
{
    int CountTokens(string text);
}
