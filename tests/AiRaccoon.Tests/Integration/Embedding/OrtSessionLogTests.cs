using System.Runtime.InteropServices;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.ML.OnnxRuntime;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Sessions built through <see cref="OrtSessionLog" /> log one node-placement line per session in
///     place of ONNX Runtime's generic "Some nodes were not assigned" warning.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class OrtSessionLogTests
{
    private const string GenericWarning = "Some nodes were not assigned to the preferred execution providers";

    [RetryFact]
    public void Create_CpuSession_LogsAllNodesOnCpu()
    {
        var logger = new FakeLogger<OrtSessionLogTests>();
        using var options = new SessionOptions();

        using var session = OrtSessionLog.Create(TestData.MiniLmModelPath(), options, logger);

        var placement = logger.Collector.GetSnapshot().Single(r => r.Id.Id == 450);
        placement.Level.ShouldBe(LogLevel.Information);
        placement.Message.ShouldMatch(@": CPU: all \d+ nodes$");
    }

    [RetryFact]
    public void Create_CoreMlSession_LogsTheSplitInsteadOfTheGenericWarning()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Assert.Skip("the CoreML provider runs on macOS on Apple Silicon only");
        }

        var logger = new FakeLogger<OrtSessionLogTests>();
        using var options = new SessionOptions();
        options.AppendExecutionProvider("CoreML", new Dictionary<string, string> { ["ModelFormat"] = "MLProgram" });

        using var session = OrtSessionLog.Create(TestData.MiniLmModelPath(), options, logger);

        var records = logger.Collector.GetSnapshot();
        records.Single(r => r.Id.Id == 450).Message
            .ShouldMatch(@": CoreML: \d+/\d+ original nodes in \d+ fused partitions; CPU: \d+ — ");
        records.ShouldNotContain(r => r.Message.Contains(GenericWarning, StringComparison.Ordinal));
    }
}
