namespace AiRaccoon.Core.Observability;

/// <summary>
///     Observability port for a background pass. One generic port rather than one method per
///     measurement (docs/work/2026-08-09-otlp-fix-plan.md D4, ruled option A); it lives in Core so
///     Infrastructure's hosted services can take it and a recording fake can stand in.
/// </summary>
public interface IOperationTelemetry
{
    /// <summary>Opens a scope for one pass of <paramref name="operation" /> (e.g. "extraction.pass").</summary>
    IOperationScope Begin(string operation);
}

/// <summary>
///     One background pass: a span for its lifetime, and exactly one duration + outcome
///     measurement. A scope disposed without <see cref="Succeeded" /> or <see cref="Failed" />
///     records result=unknown rather than nothing — an abandoned pass is a hole, not a success.
/// </summary>
public interface IOperationScope : IDisposable
{
    /// <summary>Adds a span attribute. Span only: the metrics carry operation and result alone.</summary>
    void Tag(string key, string value);

    void Succeeded();

    void Failed(Exception exception);
}
