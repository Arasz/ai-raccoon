using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory.Filtering;

namespace AiRaccoon.Infrastructure.Embedding;

public sealed class BundledNoiseVectorProvider : INoiseVectorProvider
{
    private static readonly Lazy<IReadOnlyList<NoiseVector>> CachedVectors = new(LoadVectors);

    public Task<IReadOnlyList<NoiseVector>> GetNoiseVectorsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CachedVectors.Value);
    }

    private static IReadOnlyList<NoiseVector> LoadVectors()
    {
        var vectors = new List<NoiseVector>
        {
            new("hermes_process_completion_log", GenerateProcessLogVector()),
            new("terminal_statusline_capture", GenerateStatuslineVector()),
            new("cli_arg_parse_error", GenerateCliErrorVector())
        };

        return vectors;
    }

    private static float[] GenerateProcessLogVector()
    {
        var vec = new float[384];
        vec[0] = 0.95f;
        vec[1] = 0.05f;
        return vec;
    }

    private static float[] GenerateStatuslineVector()
    {
        var vec = new float[384];
        vec[0] = 0.90f;
        vec[1] = 0.10f;
        return vec;
    }

    private static float[] GenerateCliErrorVector()
    {
        var vec = new float[384];
        vec[0] = 0.88f;
        vec[1] = 0.12f;
        return vec;
    }
}
