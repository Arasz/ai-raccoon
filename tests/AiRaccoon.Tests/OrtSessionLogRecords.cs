using Microsoft.Extensions.Logging.Testing;

namespace AiRaccoon.Tests;

/// <summary>Filters out the lines every ONNX Runtime session logs while it is built (EventIds 450-452), for tests
/// that assert on what a generator itself logs.</summary>
internal static class OrtSessionLogRecords
{
    public static IReadOnlyList<FakeLogRecord> WithoutOrtSessionLines(this FakeLogCollector collector) =>
        collector.GetSnapshot().Where(record => record.Id.Id is < 450 or > 452).ToList();
}
