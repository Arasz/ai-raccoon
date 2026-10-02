using AiRaccoon.Core.Observability;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Shared skeleton for the background hosted services: telemetry pass wrapper, timer tick loop and
///     "read a setting, else fall back" — extension methods over delegates, no state of their own.
/// </summary>
internal static class BackgroundLoop
{
    /// <summary>Runs a pass under a telemetry scope: Succeeded, Failed (then rethrown), or abandoned on shutdown.</summary>
    public static Task RunPassAsync(this IOperationTelemetry telemetry, string operation,
        Func<IOperationScope, CancellationToken, Task> runPass, CancellationToken cancellationToken) =>
        telemetry.RunPassCountingFailuresAsync(operation, async (pass, ct) =>
        {
            await runPass(pass, ct);
            return 0;
        }, cancellationToken);

    /// <summary>Like <see cref="RunPassAsync" />, but a pass returning failures above zero ends PartiallyFailed.</summary>
    public static async Task RunPassCountingFailuresAsync(this IOperationTelemetry telemetry, string operation,
        Func<IOperationScope, CancellationToken, Task<int>> runPass, CancellationToken cancellationToken)
    {
        using var pass = telemetry.Begin(operation);
        try
        {
            var failures = await runPass(pass, cancellationToken);
            if (failures > 0)
            {
                pass.PartiallyFailed(failures);
            }
            else
            {
                pass.Succeeded();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown cut the pass short: abandoned, not failed
        }
        catch (Exception ex)
        {
            pass.Failed(ex);
            throw;
        }
    }

    /// <summary>
    ///     Runs <paramref name="runOnce" /> on every tick until cancelled. A failed pass goes to
    ///     <paramref name="onError" /> and the loop continues; <paramref name="onPassCompleted" /> fires after every
    ///     pass; when <paramref name="readInterval" /> is given the timer period is re-read after each pass.
    /// </summary>
    public static async Task RunTicksAsync(this PeriodicTimer timer, Func<CancellationToken, Task> runOnce,
        Action<Exception> onError, CancellationToken cancellationToken, Action? onPassCompleted = null,
        Func<CancellationToken, Task<TimeSpan>>? readInterval = null, Action? onIntervalRead = null)
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await runOnce(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                onError(ex);
            }
            finally
            {
                onPassCompleted?.Invoke();
            }

            if (readInterval is null)
            {
                continue;
            }

            // Re-read the interval so config changes apply without a restart.
            timer.Period = await readInterval(cancellationToken);
            onIntervalRead?.Invoke();
        }
    }

    /// <summary>Reads a value; any failure but shutdown goes to <paramref name="onError" /> and yields the fallback.</summary>
    public static async Task<T> ReadOrFallbackAsync<T>(Func<CancellationToken, Task<T>> read, T fallback,
        Action<Exception> onError, CancellationToken cancellationToken)
    {
        try
        {
            return await read(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            onError(ex);
            return fallback;
        }
    }
}
