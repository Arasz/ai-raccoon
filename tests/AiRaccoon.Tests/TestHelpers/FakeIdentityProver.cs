using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A scriptable verifier: records every endpoint it was asked to prove and answers with the
///     failure the test chose (null = proven). Per-call answers let a test make the configured
///     listener fail and the private fallback child succeed, exactly as a real root's key would.
/// </summary>
internal sealed class FakeIdentityProver : IIdentityProver
{
    private readonly Queue<IdentityProofFailure?> _pending = new();
    private IdentityProofFailure? _last;

    /// <summary>The first call answers <paramref name="failure" /> (null = proven).</summary>
    public FakeIdentityProver(IdentityProofFailure? failure = null) => _pending.Enqueue(failure);

    public List<Uri> Calls { get; } = [];

    /// <summary>Queue the answer for the next call; once the queue empties, the last answer repeats.</summary>
    public void AnswerNext(IdentityProofFailure? failure) => _pending.Enqueue(failure);

    public Task<IdentityProofFailure?> ProveAsync(Uri endpoint, CancellationToken ctx)
    {
        Calls.Add(endpoint);
        if (_pending.Count > 0)
        {
            _last = _pending.Dequeue();
        }

        return Task.FromResult(_last);
    }
}
