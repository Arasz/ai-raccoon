using FluentValidation;

namespace AiRaccoon.Core.Memory;

public sealed record SearchQuery(
    string ProjectId,
    string Query,
    SearchScope Scope = SearchScope.All,
    string? WorkspaceId = null,
    int Limit = 20,
    double MinScore = 0.7)
{
    public sealed class Validator : AbstractValidator<SearchQuery>
    {
        public Validator()
        {
            RuleFor(x => x.ProjectId).NotNull().NotEmpty();
            RuleFor(x => x.Query).NotNull().NotEmpty();
            RuleFor(x => x.Limit).GreaterThan(0);
            RuleFor(x => x.MinScore).InclusiveBetween(0.0, 1.0);
        }
    }
}
