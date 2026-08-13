namespace AiRaccoon.Hosting.Common;

public interface IServerProbe
{
    Task<bool> RespondsAsync(int port, CancellationToken ctx);
    Task<bool> RespondsAsync(Uri endpoint, CancellationToken ctx);
}
