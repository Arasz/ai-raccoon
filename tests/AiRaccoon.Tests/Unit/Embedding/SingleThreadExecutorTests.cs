using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     ADR-0110: the onnxruntime MLX plugin is thread-affine — a session may only be run from the
///     exact OS thread it first ran on, and calling it from another throws
///     ("MLX eval is thread-affine"). <see cref="SingleThreadExecutor" /> is what pins every call a
///     session makes (construction, Run, Dispose) to one dedicated thread, verified here with plain
///     managed work instead of a real ONNX session.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SingleThreadExecutorTests
{
    [Fact]
    public void Run_RepeatedCalls_AllExecuteOnTheSameManagedThread()
    {
        using var executor = new SingleThreadExecutor("test-executor");

        var threadIds = Enumerable.Range(0, 20).Select(_ => executor.Run(() => Environment.CurrentManagedThreadId)).ToList();

        threadIds.Distinct().ShouldHaveSingleItem();
        threadIds[0].ShouldNotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void Run_ReturnsTheWorkResult() =>
        new SingleThreadExecutor("test-executor").Run(() => 21 * 2).ShouldBe(42);

    [Fact]
    public void Run_WorkThrows_PropagatesTheExceptionToTheCaller()
    {
        using var executor = new SingleThreadExecutor("test-executor");

        var ex = Should.Throw<InvalidOperationException>(() => executor.Run<int>(() => throw new InvalidOperationException("boom")));

        ex.Message.ShouldBe("boom");
    }

    [Fact]
    public void Run_AfterDispose_Throws()
    {
        var executor = new SingleThreadExecutor("test-executor");
        executor.Dispose();

        Should.Throw<ObjectDisposedException>(() => executor.Run(() => 1));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var executor = new SingleThreadExecutor("test-executor");
        executor.Dispose();

        Should.NotThrow(() => executor.Dispose());
    }
}
