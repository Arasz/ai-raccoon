using System.Text;
using AiRaccoon.Core.Embedding;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Embedding.NeuralEngine;

/// <summary>
///     The bundled engine under <c>device coreml</c> (ADR-0118): WebGPU serves while the Neural Engine
///     bucket sessions compile in the background, and rows move to them only once all load and pass a
///     parity probe against WebGPU. A failure, timeout or probe miss keeps WebGPU until restart.
/// </summary>
internal sealed partial class NeuralEngineEmbeddingGenerator : ILocalEmbeddingGenerator
{
    /// <summary>How long the background compile may take before WebGPU is kept for good.</summary>
    public static readonly TimeSpan CompileDeadline = TimeSpan.FromMinutes(5);

    /// <summary>The CLS cosine every probe row must reach between the Neural Engine and WebGPU.</summary>
    public const double ProbeCosine = 0.999;

    private const string ProbeSentence = "The drain embeds pending rows one at a time on the Neural Engine.";

    private readonly ILocalEmbeddingGenerator _webGpu;
    private readonly IEmbeddingTokenizer _tokenizer;
    private readonly bool _normalize;
    private readonly ICoreMlSessionFactory _factory;
    private readonly CoreMlCache _cache;
    private readonly TimeProvider _time;
    private readonly TimeSpan _deadline;
    private readonly ILogger _logger;
    private readonly string _webGpuProvider;
    private readonly Lock _serving = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _settled;
    private Task _compile = Task.CompletedTask;
    private Loaded? _neuralEngine;
    private ILocalEmbeddingGenerator? _overflow;
    private bool _disposed;

    internal NeuralEngineEmbeddingGenerator(ILocalEmbeddingGenerator webGpu, IEmbeddingTokenizer tokenizer, bool normalize,
        ICoreMlSessionFactory factory, CoreMlCache cache, TimeProvider time, TimeSpan deadline, ILogger logger)
    {
        Guard.IsNotNull(webGpu);
        Guard.IsNotNull(tokenizer);
        Guard.IsNotNull(factory);
        Guard.IsNotNull(cache);
        Guard.IsNotNull(time);
        Guard.IsNotNull(logger);
        _webGpu = webGpu;
        _tokenizer = tokenizer;
        _normalize = normalize;
        _factory = factory;
        _cache = cache;
        _time = time;
        _deadline = deadline;
        _logger = logger;
        _webGpuProvider = webGpu.ExecutionProvider;
        IntraOpThreads = webGpu.IntraOpThreads;

        Transition(NeuralEngineTrigger.Started, "device coreml");
        Log.CompileStarted(_logger, cache.BucketDirectory(CoreMlGraph.Buckets[0]));
        _settled = Task.Run(SwitchAsync);
    }

    /// <summary>The background compile's state machine; every move it made is in its history.</summary>
    public NeuralEngineSwitch Switch { get; } = new();

    public string ExecutionProvider => Switch.State switch
    {
        NeuralEngineState.NeuralEngineServing => "CoreML",
        NeuralEngineState.Refused => $"{_webGpuProvider} (CoreML refused: {Switch.History[^1].Reason})",
        _ => $"{_webGpuProvider} (CoreML compiling)"
    };

    public int IntraOpThreads { get; }

    /// <summary>Why this platform cannot run the Neural Engine path, or null when it can.</summary>
    public static string? RefusalReason(bool macArm64, bool graphPresent) =>
        !macArm64 ? "requires macOS on Apple Silicon"
        : !graphPresent ? "graph missing"
        : null;

    /// <summary>Completes once the background compile has settled, with the state it settled in. Test hook.</summary>
    internal async Task<NeuralEngineState> WaitUntilSettledAsync(CancellationToken cancellationToken)
    {
        await _settled.WaitAsync(cancellationToken);
        return Switch.State;
    }

    /// <summary>The background session load, which can outlive <see cref="Dispose" /> by one bucket compile. Test hook.</summary>
    internal Task Compile => Volatile.Read(ref _compile);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(values);
        var texts = values.ToList();
        return Task.Run(() => Serve(texts, options, cancellationToken), cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        lock (_serving)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_neuralEngine is null)
            {
                _webGpu.Dispose();
            }

            _neuralEngine?.Dispose();
            _overflow?.Dispose();
        }

        _stop.Cancel();
    }

    private GeneratedEmbeddings<Embedding<float>> Serve(List<string> texts, EmbeddingGenerationOptions? options,
        CancellationToken cancellationToken)
    {
        lock (_serving)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_neuralEngine is not { } neuralEngine)
            {
                return _webGpu.GenerateAsync(texts, options, cancellationToken).GetAwaiter().GetResult();
            }

            var embeddings = new GeneratedEmbeddings<Embedding<float>>(texts.Count);
            foreach (var text in texts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ids = _tokenizer.EncodeToIds(text, true);
                if (ids.Count > LengthBuckets.CoreMlWindow)
                {
                    _overflow ??= _factory.CreateOverflow();
                    embeddings.Add(_overflow.GenerateAsync([text], options, cancellationToken).GetAwaiter().GetResult()[0]);
                    continue;
                }

                var vector = neuralEngine.Run(ids);
                embeddings.Add(new Embedding<float>(_normalize ? EmbeddingMath.L2Normalize(vector) : vector));
            }

            return embeddings;
        }
    }

    private async Task SwitchAsync()
    {
        var started = _time.GetTimestamp();
        List<ProbeRow> probes;
        try
        {
            probes = [.. CoreMlGraph.Buckets.Select(ProbeRowFor)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Refuse(NeuralEngineTrigger.LoadFailed, ex.Message);
            return;
        }

        var reference = GenerateAsync(probes.Select(p => p.Text), cancellationToken: _stop.Token);
        var compile = Task.Factory.StartNew(() => Load(probes, _stop.Token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Volatile.Write(ref _compile, compile);
        Loaded loaded;
        try
        {
            loaded = await compile.WaitAsync(_deadline, _time, _stop.Token);
        }
        catch (TimeoutException)
        {
            _stop.Cancel();
            DisposeWhenLoaded(compile, reference);
            Refuse(NeuralEngineTrigger.TimedOut, $"the Neural Engine sessions did not load within {_deadline.TotalMinutes:0.#} minutes");
            return;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            DisposeWhenLoaded(compile, reference);
            return;
        }
        catch (Exception ex)
        {
            Observe(reference);
            Refuse(NeuralEngineTrigger.LoadFailed, ex.Message);
            return;
        }

        string? miss;
        try
        {
            var expected = await reference;
            miss = ProbeMiss(probes, loaded.ProbeVectors, expected);
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            miss = $"the WebGPU reference failed: {ex.Message}";
        }
        catch (Exception)
        {
            loaded.Dispose();
            return;
        }

        if (miss is not null)
        {
            loaded.Dispose();
            Refuse(NeuralEngineTrigger.ProbeFailed, miss);
            return;
        }

        NeuralEngineTransition passed;
        lock (_serving)
        {
            if (_disposed)
            {
                loaded.Dispose();
                return;
            }

            Switch.Fire(NeuralEngineTrigger.SessionsLoadedAndProbePassed,
                $"{CoreMlGraph.Buckets.Count} buckets loaded; every probe row reached cosine {ProbeCosine}", _time.GetUtcNow());
            passed = Switch.History[^1];
            _neuralEngine = loaded;
            _webGpu.Dispose();
        }

        _cache.WriteStatus(passed);
        var compiled = loaded.Lease.Compiling;
        loaded.Lease.MarkComplete();
        _cache.Prune();
        Log.NeuralEngineServing(_logger, compiled ? "compiled" : "loaded from cache", _time.GetElapsedTime(started).TotalSeconds);
    }

    private Loaded Load(List<ProbeRow> probes, CancellationToken cancellationToken)
    {
        var lease = _cache.Acquire(cancellationToken);
        var sessions = new Dictionary<int, ICoreMlBucketSession>();
        try
        {
            foreach (var bucket in CoreMlGraph.Buckets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sessions[bucket] = _factory.CreateBucket(bucket, _cache.BucketDirectory(bucket));
            }

            var loaded = new Loaded(sessions, lease);
            loaded.ProbeVectors = [.. probes.Select(p => loaded.Run(p.Ids))];
            return loaded;
        }
        catch
        {
            foreach (var session in sessions.Values)
            {
                session.Dispose();
            }

            lease.Dispose();
            throw;
        }
    }

    /// <summary>A fixed row whose token count pads to <paramref name="bucket" />.</summary>
    private ProbeRow ProbeRowFor(int bucket)
    {
        var target = bucket - LengthBuckets.CoreMlStep / 2;
        var text = new StringBuilder(ProbeSentence);
        var ids = _tokenizer.EncodeToIds(text.ToString(), true);
        while (ids.Count < target)
        {
            text.Append(' ').Append(ProbeSentence);
            ids = _tokenizer.EncodeToIds(text.ToString(), true);
        }

        if (LengthBuckets.PaddedLength(ids.Count, LengthBuckets.CoreMlWindow, LengthBuckets.CoreMlStep) != bucket)
        {
            throw new InvalidOperationException($"the probe row for bucket {bucket} tokenized to {ids.Count} tokens");
        }

        return new ProbeRow(bucket, text.ToString(), ids);
    }

    private static string? ProbeMiss(List<ProbeRow> probes, float[][] neuralEngine, GeneratedEmbeddings<Embedding<float>> webGpu)
    {
        for (var i = 0; i < probes.Count; i++)
        {
            var cosine = Cosine(neuralEngine[i], webGpu[i].Vector.Span);
            if (!(cosine >= ProbeCosine))
            {
                return $"bucket {probes[i].Bucket}: probe cosine {cosine:F6} is below {ProbeCosine}";
            }
        }

        return null;
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dot / Math.Sqrt(normA * normB);
    }

    private void Refuse(NeuralEngineTrigger trigger, string reason)
    {
        Transition(trigger, reason);
        Log.Refused(_logger, trigger, reason);
    }

    private void Transition(NeuralEngineTrigger trigger, string reason)
    {
        Switch.Fire(trigger, reason, _time.GetUtcNow());
        _cache.WriteStatus(Switch.History[^1]);
    }

    private static void DisposeWhenLoaded(Task<Loaded> compile, Task reference)
    {
        Observe(reference);
        _ = compile.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                task.Result.Dispose();
            }

            return task.Exception;
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private sealed record ProbeRow(int Bucket, string Text, IReadOnlyList<int> Ids);

    /// <summary>The loaded bucket sessions and the cache lock they were loaded under.</summary>
    private sealed class Loaded(Dictionary<int, ICoreMlBucketSession> sessions, CoreMlCacheLease lease) : IDisposable
    {
        public CoreMlCacheLease Lease { get; } = lease;

        public float[][] ProbeVectors { get; set; } = [];

        /// <summary>Runs one row at batch 1, padded with id 0 and mask 0 to its bucket.</summary>
        public float[] Run(IReadOnlyList<int> ids)
        {
            var length = LengthBuckets.PaddedLength(ids.Count, LengthBuckets.CoreMlWindow, LengthBuckets.CoreMlStep);
            var inputIds = new long[length];
            var mask = new long[length];
            for (var i = 0; i < ids.Count; i++)
            {
                inputIds[i] = ids[i];
                mask[i] = 1;
            }

            return sessions[length].Run(inputIds, mask);
        }

        public void Dispose()
        {
            foreach (var session in sessions.Values)
            {
                session.Dispose();
            }

            Lease.Dispose();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 436, Level = LogLevel.Information,
            Message = "Loading the Neural Engine sessions in the background (cache {CacheDirectory}); WebGPU serves until they are ready.")]
        public static partial void CompileStarted(ILogger logger, string cacheDirectory);

        [LoggerMessage(EventId = 437, Level = LogLevel.Information,
            Message = "Embedding now runs on the Neural Engine: sessions {How} in {Seconds:0.0} s and passed the parity probe.")]
        public static partial void NeuralEngineServing(ILogger logger, string how, double seconds);

        [LoggerMessage(EventId = 438, Level = LogLevel.Warning,
            Message = "The Neural Engine was refused ({Trigger}): {Reason}. WebGPU keeps serving until the server restarts.")]
        public static partial void Refused(ILogger logger, NeuralEngineTrigger trigger, string reason);
    }
}
