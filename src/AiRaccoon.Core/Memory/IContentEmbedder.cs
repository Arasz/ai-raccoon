namespace AiRaccoon.Core.Memory;

using System.Threading;
using System.Threading.Tasks;

public interface IContentEmbedder
{
    Task<float[]?> EmbedContentAsync(string content, CancellationToken cancellationToken = default);
}
