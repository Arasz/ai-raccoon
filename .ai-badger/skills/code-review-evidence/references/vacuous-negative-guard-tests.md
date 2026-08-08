# Vacuous negative guard tests — worked case (PR #748, OpenRouter swap)

Context: PR #748 in the reference repo flipped the deployment-level LLM
fallback from Anthropic to OpenRouter. Acceptance criterion: "Startup throws
iff the OpenRouter config section is missing; Anthropic optional." The PR
added `DeployedConfigurationTests.A_deployed_build_without_OpenRouter_ApiKey_fails_to_compose`
to pin the deployed-missing-key case at the API layer. It passed — for the
wrong reason.

## The three checks that exposed it

1. **Entry point.** The test called `services.AddApiServices(configuration)`.
   The OpenRouter guard lives in
   `InfrastructureDependencies.RegisterLlmConnectionInfrastructure`
   (`Get<T>() ?? ThrowHelper.ThrowInvalidOperationException<T>("Missing required
   configuration section 'OpenRouter'.")`), reachable only through
   `AddInfrastructure` (line 72 of InfrastructureDependencies.cs). `AddApiServices`
   (ApiDependencies.cs) never calls `AddInfrastructure` — the guard is unreachable
   from the test.
2. **Earlier throwers.** The fixture `DeployedAppSettingsWithoutOpenRouterKey`
   = `DeployedAppSettings` minus `OpenRouter:ApiKey`, and `DeployedAppSettings`
   contains no `UserAuth` keys (appsettings.json ships none — verified by grep).
   `AddApiServices`'s first config-touching call is `RegisterAuth` →
   `configuration.GetRequiredSection(UserAuthOptions.Section)` (ApiDependencies.cs:127),
   which throws `InvalidOperationException`. The test asserts exactly
   `InvalidOperationException` → green, vacuously. It would be green on the
   pre-change code (OpenRouter optional) and green if the guard were deleted.
   The plan's claim "that negative test is RED against pre-WP1 code" was
   therefore false — a falsifiable claim, falsified by the fixture audit.
3. **Guard semantics.** Even correctly scoped (calling `AddInfrastructure` with
   a fixture satisfying Cosmos/Blob/UserSecrets/Salary), the guard would NOT
   fire: the Api's shipped `appsettings.json` carries a keyless `OpenRouter`
   section (model ids only). `GetSection("OpenRouter")` is non-null →
   `Get<OpenRouterLlmClientOptions>()` binds a non-null object with
   `ApiKey = null` → `??` short-circuit never triggers.

## The binder probe (settles "does Get<T> throw for missing required members?")

.NET ConfigurationBinder does NOT throw when a `required` member is absent
from a present section — it binds `null`. Verified with a throwaway console
in /tmp (outside the repo):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="10.0.0" />
  </ItemGroup>
</Project>
```

```csharp
using Microsoft.Extensions.Configuration;

var opts = new ConfigurationBuilder().AddJsonFile("probe.json").Build();
var o = opts.GetSection("OpenRouter").Get<OpenRouterOpts>();
Console.WriteLine($"Bound: {o is not null}; ApiKey null? {o?.ApiKey is null}");

public sealed record OpenRouterOpts
{
    public const string Section = "OpenRouter";
    public required string ApiKey { get; init; }
    public string CheapModelId { get; init; } = "xiaomi/mimo-v2.5-pro";
}
```

`probe.json` = `{ "OpenRouter": { "CheapModelId": "xiaomi/mimo-v2.5-pro" } }`
→ output `Bound: True; ApiKey null? True`. (First probe run printed
`ApiKey: ***` — output masking of a secret-shaped string; use the explicit
`is null` check, never value printing.)

## Consequence

The realistic deployed failure — Terraform `OpenRouter__ApiKey` app setting
lost → section present-but-keyless (appsettings.json always ships it) → null
ApiKey binds → fallback client constructed with a null key → every
unconfigured-user LLM call 401s at runtime — is NOT caught at startup by the
section-absent guard. "Startup throws iff the section is missing" holds only
for whole-section absence, which no real host exhibits.

## Honest fix (what the review recommended)

1. Production: guard the bound VALUE after binding, e.g.
   `if (string.IsNullOrWhiteSpace(openRouterOptions.ApiKey))
   ThrowHelper.ThrowInvalidOperationException(...)` — a present-but-keyless
   section then fails at startup.
2. Test: call `services.AddInfrastructure(configuration)` (the method that
   contains the guard) with a fixture satisfying every other required section
   (Cosmos, Blob, UserSecrets keyring) minus `OpenRouter:ApiKey`. Without fix
   (1) this goes RED — which is the correct TDD signal.
3. Keep the whole-section-absent negative test (it IS honest: in-memory config
   without the section → `Get` returns null → guard fires with the exact
   message).

## Reusable checklist for any "fails to compose" negative test

- [ ] The test calls the registration method that contains the guard
      (grep the guard's file for which public entry point invokes it).
- [ ] The fixture satisfies every OTHER required input, so the asserted throw
      can only come from the guard under test (list the other `GetRequiredSection`
      / `?? ThrowHelper` sites that run earlier in that entry point).
- [ ] The guard actually fires for the claimed scenario — check binder
      semantics (`required` members bind null, they don't throw) whenever the
      shipped config makes the section always-present-but-possibly-keyless.
- [ ] The plan's "this test was RED before the change" claim survives the
      fixture audit; if it can't have been RED, the test is vacuous by
      definition.
