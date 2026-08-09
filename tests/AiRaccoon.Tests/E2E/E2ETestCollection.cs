using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Some E2E tests mutate the process environment (e.g. OTEL_EXPORTER_OTLP_ENDPOINT) to boot
///     the real server, so the whole collection runs serially against one another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2ETestCollection
{
    public const string Name = "E2E";
}
