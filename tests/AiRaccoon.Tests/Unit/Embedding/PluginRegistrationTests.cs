using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>The process-wide plugin registration guard (CUDA, MLX), seamed off from a real ONNX Runtime with fakes.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PluginRegistrationTests
{
    [Fact]
    public void EnsureRegistered_SameNameAndPathTwice_RegistersOnce()
    {
        var name = UniqueName();
        var calls = new List<(string Name, string Path)>();

        OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/a.so", (n, p) => calls.Add((n, p))).ShouldBeNull();
        OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/a.so", (n, p) => calls.Add((n, p))).ShouldBeNull();

        calls.ShouldBe([(name, "/libs/a.so")]);
    }

    [Fact]
    public void EnsureRegistered_SameNameDifferentPath_IsRefused_WithoutRegistering()
    {
        var name = UniqueName();
        var calls = 0;
        OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/a.so", (_, _) => calls++);

        var refusal = OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/b.so", (_, _) => calls++);

        refusal.ShouldBe($"a different {name} provider library is already registered in this process (restart the server)");
        calls.ShouldBe(1);
    }

    [Fact]
    public void EnsureRegistered_FailedRegistration_IsNotRecorded()
    {
        var name = UniqueName();

        Should.Throw<DllNotFoundException>(() =>
            OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/broken.so", (_, _) => throw new DllNotFoundException("broken")));
        var calls = 0;
        var refusal = OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/fixed.so", (_, _) => calls++);

        refusal.ShouldBeNull();
        calls.ShouldBe(1);
    }

    private static string UniqueName() => $"TestExecutionProvider{Guid.NewGuid():N}";
}
