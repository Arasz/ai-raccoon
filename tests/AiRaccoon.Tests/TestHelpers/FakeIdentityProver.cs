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
    private readonly Queue<IdentityProofFailure?> _answers = new();

    /// <summary>Every answer is <paramref name="failure" /> (null = proven).</summary>
    public FakeIdentityProver(IdentityProofFailure? failure = null) => _answers.Enqueue(failure);

    public List<Uri> Calls { get; } = [];

    /// <summary>Answer the next call with <paramref name="failure" />; the last answer repeats.</summary>
    public void AnswerNext(IdentityProofFailure? failure) => _answers.Enqueue(failure);

    public Task<IdentityProofFailure?> ProveAsync(Uri endpoint, CancellationToken ctx)
    {
        Calls.Add(endpoint);
        return Task.FromResult(_answers.Count > 1 ? _answers.Dequeue() : _answers.Peek());
    }
}
