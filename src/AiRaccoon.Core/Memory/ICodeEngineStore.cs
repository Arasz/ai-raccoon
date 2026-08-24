namespace AiRaccoon.Core.Memory;

/// <summary>
///     Activates the code corpus's embedding engine (§3.3 D-E9): commits embedding.codeModel and
///     embedding.codeEngine, and invalidates every currently-embedded code row to 'pending' — all
///     in ONE transaction, so the vec_code_pending trigger empties vec_code at commit with no
///     stale-vector window. No outbox, no relay, no ToolGate interaction — a separate maintenance
///     job drains the pending rows. Split out for the same reason <see cref="IModelMigrationStore" />
///     was (ADR-0075/0076): the CLI reaches it through the server instead of opening the bank.
/// </summary>
public interface ICodeEngineStore
{
    /// <summary>
    ///     Commits embedding.codeModel = <paramref name="directory" />, embedding.codeEngine =
    ///     its fingerprint (the same derivation <c>IEmbeddingService.EngineFingerprint</c> uses),
    ///     and embedding.codeDimensions; reconciles vec_code to the manifest's dimension; and marks
    ///     every currently-embedded code_entries row pending — all in ONE transaction. The
    ///     directory's manifest must already be valid (the chunk-budget refusal happens here).
    /// </summary>
    Task<EmbeddingConfig> ActivateCodeEngineAsync(string directory, CancellationToken cancellationToken = default);
}

/// <summary>
///     Thrown when <see cref="ICodeEngineStore.ActivateCodeEngineAsync" /> refuses a directory
///     (#472: missing/invalid manifest, the wrong dimension count, or a chunk budget narrower than
///     the code chunker's) — an <see cref="InvalidOperationException" /> subtype so the settings
///     endpoint can catch it and map it to a 4xx with the reason intact, instead of it escaping as
///     a bare 500.
/// </summary>
public sealed class CodeEngineActivationRefusedException : InvalidOperationException
{
    public CodeEngineActivationRefusedException(string message) : base(message)
    {
    }

    public CodeEngineActivationRefusedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
