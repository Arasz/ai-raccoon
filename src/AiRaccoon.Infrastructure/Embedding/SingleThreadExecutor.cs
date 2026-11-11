using System.Collections.Concurrent;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Runs every unit of work on one dedicated, long-lived OS thread — for a native resource
///     that is thread-affine (the onnxruntime MLX plugin: "MLX eval is thread-affine, use one
///     InferenceSession per thread", ADR-0110) rather than merely non-reentrant. Serializes as a
///     side effect: one thread cannot run two calls at once.
/// </summary>
internal sealed class SingleThreadExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public SingleThreadExecutor(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Test hook: whether <see cref="Dispose" /> has run.</summary>
    internal bool IsDisposed { get; private set; }

    /// <summary>Test hook: whether the dedicated thread is still running.</summary>
    internal bool IsThreadAlive => _thread.IsAlive;

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
    }

    public T Run<T>(Func<T> work)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }
}
