using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     <see cref="NodePlacement" /> turns ONNX Runtime's verbose "Node placements" block (and CoreML's
///     GetCapability line) into one summary per session. The input lines are ORT 1.30.0's message text
///     as the env logging callback receives it (docs/work/2026-10-23-ort-node-placement-logging.md).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NodePlacementTests
{
    private const string CoreMlCapability =
        "CoreMLExecutionProvider::GetCapability, number of partitions supported by CoreML: 3 number of nodes in the graph: 12 " +
        "number of nodes supported by CoreML: 7";

    private static NodePlacement Feed(params string[] lines)
    {
        var placement = new NodePlacement();
        foreach (var line in lines)
        {
            placement.Add(line);
        }

        return placement;
    }

    [Fact]
    public void Summary_CoreMlSession_CountsOriginalNodesAndListsCpuOps()
    {
        var placement = Feed(
            CoreMlCapability,
            "Node placements",
            " Node(s) placed on [CoreMLExecutionProvider]. Number of nodes: 3",
            "  1362_CoreML_1362_0 (1362_CoreML_1362_0)",
            "  1362_CoreML_1362_1 (1362_CoreML_1362_1)",
            "  1362_CoreML_1362_2 (1362_CoreML_1362_2)",
            " Node(s) placed on [CPUExecutionProvider]. Number of nodes: 5",
            "  Reshape (/layers.0/Reshape)",
            "  Neg (/layers.0/Neg)",
            "  Reshape (/layers.1/Reshape)",
            "  Range (/Range)",
            "  Abs (/Abs)");

        placement.Summary().ShouldBe("CoreML: 7/12 original nodes in 3 fused partitions; CPU: 5 — Reshape×2, Neg, Range, Abs");
    }

    [Fact]
    public void Summary_AllNodesOnOneProvider_SaysAll()
    {
        var placement = Feed("Node placements", " All nodes placed on [CPUExecutionProvider]. Number of nodes: 399");

        placement.Summary().ShouldBe("CPU: all 399 nodes");
    }

    [Fact]
    public void Summary_PlainProvider_ListsItsOpsBeforeCpu()
    {
        var placement = Feed(
            " Node(s) placed on [CPUExecutionProvider]. Number of nodes: 1",
            "  Shape (/Shape)",
            " Node(s) placed on [WebGpuExecutionProvider]. Number of nodes: 3",
            "  MatMul (/a)",
            "  Add (/b)",
            "  MatMul (/c)");

        placement.Summary().ShouldBe("WebGpu: 3 — MatMul×2, Add; CPU: 1 — Shape");
    }

    [Fact]
    public void Summary_FusedPluginWithoutCapabilityLine_CountsPartitions()
    {
        var placement = Feed(
            " Node(s) placed on [MLXExecutionProvider]. Number of nodes: 2",
            "  MLX_fused_0 (MLX_fused_0)",
            "  MLX_fused_1 (MLX_fused_1)",
            " Node(s) placed on [CPUExecutionProvider]. Number of nodes: 1",
            "  MultiHeadAttention (/attn)");

        placement.Summary().ShouldBe("MLX: 2 fused partitions; CPU: 1 — MultiHeadAttention");
    }

    [Fact]
    public void Summary_ProviderWithFusedAndPlainNodes_CountsBoth()
    {
        var placement = Feed(
            " Node(s) placed on [MLXExecutionProvider]. Number of nodes: 3",
            "  MLX_fused_0 (MLX_fused_0)",
            "  Gather (/g)",
            "  MLX_fused_1 (MLX_fused_1)");

        placement.Summary().ShouldBe("MLX: 3 — 2 fused partitions, Gather");
    }

    [Fact]
    public void Summary_NoPlacementBlock_IsNull()
    {
        var placement = Feed(CoreMlCapability, "Session successfully initialized.");

        placement.Summary().ShouldBeNull();
    }

    [Fact]
    public void Add_LinesAfterTheBlock_AreNotCountedAsNodes()
    {
        var placement = Feed(
            " Node(s) placed on [CPUExecutionProvider]. Number of nodes: 1",
            "  Shape (/Shape)",
            "Use DeviceBasedPartition as default",
            "  Gather (an indented line from a later caller)");

        placement.Summary().ShouldBe("CPU: 1 — Shape");
    }
}
