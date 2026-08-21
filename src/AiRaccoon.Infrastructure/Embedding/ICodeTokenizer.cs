namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Counts tokens the way the bundled code-daemon-embed-v1 counting tokenizer will
/// (docs/work/2026-08-21-code-search-implementation-plan.md §3.3 unconfigured-engine default).</summary>
public interface ICodeTokenizer
{
    int CountTokens(string text);
}
