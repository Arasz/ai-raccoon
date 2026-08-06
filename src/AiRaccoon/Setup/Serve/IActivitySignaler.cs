namespace AiRaccoon.Setup.Serve;

/// <summary>Signals server activity so the idle watchdog can reset its deadline.</summary>
public interface IActivitySignaler
{
    /// <summary>Records that the server was used; only /mcp traffic calls this.</summary>
    void NotifyActivity();
}
