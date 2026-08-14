namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Holds the process-global env-var gate while a set of variables is overridden, and on
///     dispose restores every one of them to the value it held before — the release in a finally,
///     so no restore failure can strand the gate.
/// </summary>
public sealed class EnvScope : IAsyncDisposable
{
    private readonly (string Name, string? Original)[] _snapshots;
    private bool _released;

    private EnvScope((string Name, string? Original)[] snapshots) => _snapshots = snapshots;

    /// <summary>Takes the gate with the caller's token, then applies each override.</summary>
    public static async ValueTask<EnvScope> AcquireAsync(CancellationToken cancellationToken,
        params (string Name, string? Value)[] vars)
    {
        ArgumentNullException.ThrowIfNull(vars);
        await TestData.EnvVarGate.WaitAsync(cancellationToken);
        var scope = new EnvScope([.. vars.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name)))]);
        try
        {
            foreach (var (name, value) in vars)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }

        return scope;
    }

    public ValueTask DisposeAsync()
    {
        if (_released)
        {
            return ValueTask.CompletedTask;
        }

        _released = true;
        try
        {
            foreach (var (name, original) in _snapshots)
            {
                Environment.SetEnvironmentVariable(name, original);
            }
        }
        finally
        {
            TestData.EnvVarGate.Release();
        }

        return ValueTask.CompletedTask;
    }
}
