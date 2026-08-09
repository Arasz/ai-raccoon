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
///     One background pass: exactly one duration + outcome measurement always, but a span only
///     when the pass is worth reading — <see cref="NoteWork" /> was called, or the outcome is
///     failure or unknown (docs/work/2026-08-09-otlp-fix-plan.md WP13 fix). A scope disposed
///     without <see cref="Succeeded" /> or <see cref="Failed" /> records result=unknown rather than
///     nothing — an abandoned pass is a hole, not a success.
/// </summary>
public interface IOperationScope : IDisposable
{
    /// <summary>Adds a span attribute, applied if and when a span is recorded. Span only: the
    /// metrics carry operation and result alone.</summary>
    void Tag(string key, string value);

    /// <summary>Marks the pass as having done something worth a span. A clean, no-op success
    /// without this call records its metrics but no span.</summary>
    void NoteWork();

    void Succeeded();

    void Failed(Exception exception);
}
