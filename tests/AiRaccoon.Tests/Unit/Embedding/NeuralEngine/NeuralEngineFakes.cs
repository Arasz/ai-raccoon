using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.NeuralEngine;
using Microsoft.Extensions.AI;

namespace AiRaccoon.Tests.Unit.Embedding.NeuralEngine;

/// <summary>One token per space-separated word, plus [CLS]/[SEP] when asked.</summary>
internal sealed class WordTokenizer : IEmbeddingTokenizer
{
    public int SpecialTokenReservation => 2;

    public int CountTokens(string text) => Words(text).Length;

    public IReadOnlyList<int> EncodeToIds(string text, bool addSpecialTokens)
    {
        var ids = Words(text).Select((_, i) => 10 + i).ToList();
        if (addSpecialTokens)
        {
            ids.Insert(0, 1);
            ids.Add(2);
        }

        return ids;
    }

    public static string TextOf(int tokensWithSpecials) =>
        string.Join(' ', Enumerable.Repeat("word", tokensWithSpecials - 2));

    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>A WebGPU stand-in: returns <see cref="Vector" /> for every row, can hold a row whose text is <see cref="HoldText" />, and records whether it was disposed while a row was in flight.</summary>
internal sealed class FakeWebGpu : ILocalEmbeddingGenerator
{
    public const string HoldText = "hold";

    private int _inFlight;

    public float[] Vector { get; init; } = [1f, 0f, 0f, 0f];

    public ManualResetEventSlim Release { get; } = new(false);

    public ManualResetEventSlim Holding { get; } = new(false);

    public int Rows;

    public bool Disposed { get; private set; }

    public bool DisposedWhileInFlight { get; private set; }

    public string ExecutionProvider => "WebGPU";

    public int IntraOpThreads => 3;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        ObjectDisposedException.ThrowIf(Disposed, this);
        return Task.Run(() =>
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (texts.Contains(HoldText))
                {
                    Holding.Set();
                    Release.Wait(TimeSpan.FromSeconds(30));
                }

                Interlocked.Add(ref Rows, texts.Count);
                var result = new GeneratedEmbeddings<Embedding<float>>();
                result.AddRange(texts.Select(_ => new Embedding<float>(Vector)));
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        });
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        DisposedWhileInFlight |= Volatile.Read(ref _inFlight) > 0;
        Disposed = true;
    }
}

/// <summary>A bucket session returning a fixed vector and recording each padded length it ran.</summary>
internal sealed class FakeBucketSession(int bucket, float[] vector) : ICoreMlBucketSession
{
    public int Bucket { get; } = bucket;

    public int Runs;

    public bool Disposed { get; private set; }

    public float[] Run(long[] inputIds, long[] attentionMask)
    {
        if (inputIds.Length != Bucket || attentionMask.Length != Bucket)
        {
            throw new InvalidOperationException($"bucket {Bucket} ran a row padded to {inputIds.Length}");
        }

        Interlocked.Increment(ref Runs);
        return vector;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Builds <see cref="FakeBucketSession" />s; each bucket can be made to block, throw or return a mismatching vector.</summary>
internal sealed class FakeSessionFactory : ICoreMlSessionFactory
{
    public List<FakeBucketSession> Created { get; } = [];

    public ManualResetEventSlim? BlockUntil { get; init; }

    /// <summary>The only bucket <see cref="BlockUntil" /> holds; null holds every bucket.</summary>
    public int? BlockBucket { get; init; }

    public ManualResetEventSlim Entered { get; } = new(false);

    public int? ThrowOnBucket { get; init; }

    public int? MismatchOnBucket { get; init; }

    public FakeWebGpu Overflow { get; } = new() { Vector = [0f, 0f, 0f, 1f] };

    public int OverflowsCreated;

    public ICoreMlBucketSession CreateBucket(int bucket, string cacheDirectory)
    {
        if (BlockBucket is null || bucket == BlockBucket)
        {
            Entered.Set();
            BlockUntil?.Wait();
        }
        if (bucket == ThrowOnBucket)
        {
            throw new InvalidOperationException($"CoreML refused to compile bucket {bucket}");
        }

        Directory.CreateDirectory(cacheDirectory);
        var session = new FakeBucketSession(bucket, bucket == MismatchOnBucket ? [0f, 1f, 0f, 0f] : [2f, 0f, 0f, 0f]);
        lock (Created)
        {
            Created.Add(session);
        }

        return session;
    }

    public ILocalEmbeddingGenerator CreateOverflow()
    {
        Interlocked.Increment(ref OverflowsCreated);
        return Overflow;
    }
}
