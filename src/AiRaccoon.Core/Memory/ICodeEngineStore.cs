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
    ///     Commits embedding.codeModel = <paramref name="directory" /> and embedding.codeEngine =
    ///     its fingerprint (the same derivation <c>IEmbeddingService.EngineFingerprint</c> uses),
    ///     and marks every currently-embedded code_entries row pending. The directory's manifest
    ///     must already be validated (the 768-dim refusal happens before this is called).
    /// </summary>
    Task<EmbeddingConfig> ActivateCodeEngineAsync(string directory, CancellationToken cancellationToken = default);
}
