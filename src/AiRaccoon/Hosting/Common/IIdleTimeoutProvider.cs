namespace AiRaccoon.Hosting.Common;

public interface IIdleTimeoutProvider
{
    TimeSpan IdleTimeout { get; }
}
