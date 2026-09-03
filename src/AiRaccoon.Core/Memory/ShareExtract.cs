using FluentValidation;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     A validated `memory_share_extract` call. The tool builds one and hands it over; the mode
///     decision, the consent gate and the two pipelines live behind <see cref="IShareExtractService" />
///     so the CLI and the background extraction loop can reach them too (docs/adr/0065).
/// </summary>
public sealed record ShareExtractRequest(
    IReadOnlyList<string> ProjectIds,
    string Mode = ShareExtractRequest.ProposeMode,
    int? Limit = null,
    bool IncludeTtlRows = false,
    bool AutoPromote = false,
    bool Confirm = false)
{
    public const string ProposeMode = "propose";
    public const string PromoteMode = "promote";

    /// <summary>True when this call will write to the shared tier, by mode or by autoPromote.</summary>
    public bool Promotes => Mode == PromoteMode || AutoPromote;

    /// <summary>
    ///     The queue meta is one project's state, so it is scoped only when the call named exactly
    ///     one project — bank-wide otherwise, rather than picking a project arbitrarily.
    /// </summary>
    public string? MetaProjectId => ProjectIds.Count == 1 ? ProjectIds[0] : null;
}

/// <summary>Validation lives here rather than in the tool, so every caller gets the same answer.</summary>
public sealed class ShareExtractRequestValidator : AbstractValidator<ShareExtractRequest>
{
    public const int MaxProjects = 8;
    public const int MinLimit = 1;
    public const int MaxLimit = 50;

    public ShareExtractRequestValidator()
    {
        RuleFor(request => request.ProjectIds)
            .NotEmpty().WithMessage($"projectIds must contain 1..{MaxProjects} project ids")
            .Must(ids => ids.Count <= MaxProjects)
            .WithMessage($"projectIds must contain 1..{MaxProjects} project ids");

        // d-425 MUST-2, service leg: the tool pre-checks blanks before its per-element gate,
        // but this pipeline is reachable without MCP (see IShareExtractService) — a blank element
        // here fails validation instead of cwd-guessing at the service's own gate loop.
        RuleForEach(request => request.ProjectIds)
            .NotEmpty().WithMessage("projectIds must not contain blank ids");

        RuleFor(request => request.Mode)
            .Must(mode => mode is ShareExtractRequest.ProposeMode or ShareExtractRequest.PromoteMode)
            .WithMessage($"mode must be '{ShareExtractRequest.ProposeMode}' or '{ShareExtractRequest.PromoteMode}'");

        RuleFor(request => request.Limit)
            .InclusiveBetween(MinLimit, MaxLimit)
            .When(request => request.Limit is not null)
            .WithMessage($"limit must be between {MinLimit} and {MaxLimit}");
    }
}

/// <summary>
///     Raised when an operation that would make data visible beyond its project is asked for without
///     the explicit acknowledgement. Mapped to the `confirm-required` wire prefix.
/// </summary>
public sealed class ConfirmationRequiredException(string message) : Exception(message);

/// <summary>The share-extract pipeline, reachable without going through MCP (docs/adr/0065).</summary>
public interface IShareExtractService
{
    Task<ShareExtractResult> RunAsync(ShareExtractRequest request, CancellationToken cancellationToken = default);
}
