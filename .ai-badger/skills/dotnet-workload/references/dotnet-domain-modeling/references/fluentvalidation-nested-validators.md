# FluentValidation Nested Validators (project convention)


When a domain record needs validation (save-time, projection-time, or API-boundary), nest the `AbstractValidator<T>` class **inside** the validated type. Use `OverridePropertyName` for camelCase JSON property paths.

```csharp
using FluentValidation;

namespace MyApp.Domain.Feature;

public sealed record ProfileProjection
{
    public required string TemplateId { get; init; }
    public required string Headline { get; init; }
    public required IReadOnlyList<ProjectedPosition> Positions { get; init; }

    public sealed class Validator : AbstractValidator<ProfileProjection>
    {
        public Validator()
        {
            RuleFor(x => x.TemplateId).NotEmpty().OverridePropertyName("templateId");
            RuleFor(x => x.Headline).NotEmpty().OverridePropertyName("headline");
            RuleFor(x => x.Headline).MaximumLength(220).OverridePropertyName("headline");
            RuleFor(x => x.Positions).NotNull().OverridePropertyName("positions");
            RuleForEach(x => x.Positions)
                .SetValidator(new ProjectedPosition.Validator())
                .OverridePropertyName("positions");
        }
    }
}

public sealed record ProjectedPosition
{
    public required string Company { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    public sealed class Validator : AbstractValidator<ProjectedPosition>
    {
        public Validator()
        {
            RuleFor(x => x.Company).NotEmpty().OverridePropertyName("company");
            RuleFor(x => x.Title).NotEmpty().OverridePropertyName("title");
            RuleFor(x => x.Title).MaximumLength(100).OverridePropertyName("title");
            RuleFor(x => x.Description).NotEmpty().OverridePropertyName("description");
            RuleFor(x => x.Description).MaximumLength(2000).OverridePropertyName("description");
        }
    }
}
```

### Conventions

| Convention | Rationale |
|---|---|
| `sealed class Validator` nested inside validated type | Co-located, discoverable, no separate file |
| `OverridePropertyName("camelCase")` on every rule | JSON error paths match API contract |
| `SetValidator(new Child.Validator())` for nested records | Composable validation chains |
| `MaximumLength(n)` for external-platform field limits | Hard enforcement, not warnings |
| Validate the validator in tests: `new T.Validator().Validate(instance).IsValid` | Ensures test fixtures pass validation |

### Testing Validators

Test both positive (valid input passes) and negative (limit violations fail):

```csharp
[Fact]
public void Validator_fails_when_headline_exceeds_220_chars()
{
    var projection = new ProfileProjection
    {
        TemplateId = "cvt-1",
        Headline = new string('A', 221), // one over limit
        // ... other required fields
    };

    var result = new ProfileProjection.Validator().Validate(projection);
    result.IsValid.ShouldBeFalse();
}

[Fact]
public void Validator_passes_on_valid_projection()
{
    var projection = /* valid fixture */;

    var result = new ProfileProjection.Validator().Validate(projection);
    result.IsValid.ShouldBeTrue();
}
```

### camelCase property paths: use the global resolver, not `WithName` (FluentValidation 12)

Verified against FluentValidation 12.1.1 by reflection:

- **`WithName("camelCase")` changes the ERROR MESSAGE only — `ValidationFailure.PropertyName`
  stays the C# member name (`"Limit"`, `"ProjectId"`).** If your tests assert
  `e.PropertyName == "limit"`, `WithName` silently fails them. `OverridePropertyName` (as in the
  example above) is the per-rule way to rename `PropertyName` itself.
- **FV12 removed `FluentValidation.Internal.CamelCasePropertyNameResolver`** — it does not exist
  anymore. To make EVERY validator report camelCase paths process-wide, set the resolver once:

```csharp
ValidatorOptions.Global.PropertyNameResolver = (_, member, _) =>
    member.Name.Length == 0 ? member.Name : char.ToLowerInvariant(member.Name[0]) + member.Name[1..];
```

- In a **library** (Core/domain project), a `[ModuleInitializer]` is the natural one-time home for
  this — but it trips `CA2255` ("ModuleInitializer only intended in application code"). Either
  scope a `NoWarn` with a justification comment in the csproj, or set the resolver in a static
  constructor of a type the app is guaranteed to touch. Tests that construct validators directly
  still see the global (module initializers run on assembly load), so the camelCase path is
  consistent between app and test runs.

### Constructor guards → boundary validators (migration pattern)

When a review says "use FluentValidation for the validation you have", the clean move is to
migrate constructor guards OUT of request records and INTO nested validators invoked at the
boundary:

1. Records become plain data assignment (no `Guard.*` in ctors); nested `Validator` classes own
   the rules — single source of truth, no duplication.
2. Invoke at the ONLY production construction site: `new SearchQuery.Validator()
   .ValidateAndThrow(query)` in the MCP tool / controller before delegating.
3. This is safe only when the record has one production construction site (grep `new <Type>(`
   in src/ first). Domain-internal records (constructed by services with generated ids) and
   static-helper argument guards (`ContextNaming`, `RatingPolicy`) keep CommunityToolkit guards —
   a validator with no boundary caller is dead code.
4. A new third-party dependency on the domain layer (FluentValidation in Core) is an
   architecture-level decision: write an ADR (the repo's `docs/adr/` convention) before adding
   the package reference.

