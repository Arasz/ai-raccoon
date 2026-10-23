using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.ML.OnnxRuntime;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     <see cref="OrtSessionSink" /> receives one session's ONNX Runtime log lines while it is built: it
///     keeps the placement lines for the summary, drops the generic placement warning, and forwards
///     every other warning or error to the session's logger.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OrtSessionSinkTests
{
    private const OrtLoggingLevel Verbose = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE;
    private const OrtLoggingLevel Warning = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;

    [Fact]
    public void Finish_WithPlacementLines_LogsTheSummary()
    {
        var logger = new FakeLogger<OrtSessionSinkTests>();
        var sink = new OrtSessionSink(logger);
        sink.Add(Verbose, "session_state.cc:1379 VerifyEachNodeIsAssignedToAnEp",
            " All nodes placed on [CPUExecutionProvider]. Number of nodes: 12");

        sink.Finish("models/minilm/model.onnx");

        var record = logger.Collector.GetSnapshot().Single();
        record.Id.Id.ShouldBe(450);
        record.Level.ShouldBe(LogLevel.Information);
        record.Message.ShouldBe("ONNX Runtime node placement for minilm/model.onnx: CPU: all 12 nodes");
    }

    [Fact]
    public void Finish_WithoutPlacementLines_LogsOurOwnMessage()
    {
        var logger = new FakeLogger<OrtSessionSinkTests>();
        var sink = new OrtSessionSink(logger);

        sink.Finish("models/minilm/model.onnx");

        var record = logger.Collector.GetSnapshot().Single();
        record.Id.Id.ShouldBe(451);
        record.Level.ShouldBe(LogLevel.Information);
        record.Message.ShouldBe("ONNX Runtime did not report node placement for minilm/model.onnx");
    }

    [Fact]
    public void Add_GenericPlacementWarningAndCoreMlCapability_AreNotForwarded()
    {
        var logger = new FakeLogger<OrtSessionSinkTests>();
        var sink = new OrtSessionSink(logger);

        sink.Add(Warning, "session_state.cc:1397 VerifyEachNodeIsAssignedToAnEp",
            "Some nodes were not assigned to the preferred execution providers which may or may not have an negative impact on performance.");
        sink.Add(Warning, "coreml_execution_provider.cc:137 GetCapability",
            "CoreMLExecutionProvider::GetCapability, number of partitions supported by CoreML: 1 number of nodes in the graph: 2 " +
            "number of nodes supported by CoreML: 1");

        logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Add_OtherWarning_IsForwardedAtWarning()
    {
        var logger = new FakeLogger<OrtSessionSinkTests>();
        var sink = new OrtSessionSink(logger);

        sink.Add(Warning, "graph.cc:42 Resolve", "Initializer x appears in graph inputs");
        sink.Add(OrtLoggingLevel.ORT_LOGGING_LEVEL_INFO, "inference_session.cc:1 Initialize", "Session successfully initialized.");

        var record = logger.Collector.GetSnapshot().Single();
        record.Id.Id.ShouldBe(452);
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldBe("ONNX Runtime: Initializer x appears in graph inputs (graph.cc:42 Resolve)");
    }
}
