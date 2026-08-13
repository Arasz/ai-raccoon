namespace AiRaccoon.Core.Memory.Filtering;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public record NoiseVector(string Name, float[] Vector);

public interface INoiseVectorProvider
{
    Task<IReadOnlyList<NoiseVector>> GetNoiseVectorsAsync(CancellationToken cancellationToken = default);
}
