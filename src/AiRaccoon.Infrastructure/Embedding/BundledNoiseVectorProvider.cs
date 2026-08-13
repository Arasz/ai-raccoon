using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Bundled zero-shot noise vectors: real embeddings of canonical noise text (process completion
///     logs, terminal statuslines, CLI parse errors) — not synthetic placeholders. Embedded lazily
///     on first use so a bank can open without the model, and so a configured model governs them.
/// </summary>
public sealed class BundledNoiseVectorProvider(IContentEmbedder embedder) : INoiseVectorProvider
{
    private static readonly (string Name, string Text)[] CanonicalNoise =
    [
        ("hermes_process_completion_log",
            "[IMPORTANT: Background process proc_12345 completed normally. Command: python3 build.py]"),
        ("terminal_statusline_capture",
            "statusline 1 file changed 2 insertions 3 deletions"),
        ("cli_arg_parse_error",
            "error: unrecognized argument '--foobar'"),
    ];

    private readonly Lazy<Task<IReadOnlyList<NoiseVector>>> _vectors = new(async () =>
    {
        var vectors = new List<NoiseVector>();
        foreach (var (name, text) in CanonicalNoise)
        {
            var vector = await embedder.EmbedContentAsync(text, CancellationToken.None).ConfigureAwait(false);
            if (vector is not null)
            {
                vectors.Add(new NoiseVector(name, vector));
            }
        }

        return (IReadOnlyList<NoiseVector>)vectors;
    });

    public Task<IReadOnlyList<NoiseVector>> GetNoiseVectorsAsync(CancellationToken cancellationToken = default) =>
        _vectors.Value;
}
