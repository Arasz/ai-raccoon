namespace AiRaccoon.Core.Memory;
using System.Threading;
using System.Threading.Tasks;

public interface INoiseStore
{
    Task RecordNoiseAsync(MemoryWriteRequest request, string policyName, int expiresAtUnixSeconds, CancellationToken cancellationToken = default);
}
