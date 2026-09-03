# Reqnroll verification spike — 2026-08-02 (the project stack)

Question: does Reqnroll execute .feature scenarios on net10.0 with xunit.v3 3.2.2,
xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.8.1, TreatWarningsAsErrors +
EnforceCodeStyleInBuild, and central package management?
Result: **green — 3/3 scenarios passed**, twice (explicit versions, then CPM mode).

## Machine
macOS 26.5.2, .NET SDK 10.0.302. All work in `/tmp/spike-reqnroll` (repo untouched).

## Commands (as run)
```
dotnet new xunit -n Spike -f net10.0
rm -f Spike/UnitTest1.cs
# rewrite Spike.csproj (see below), add reqnroll.json, Spike.feature, SpikeSteps.cs
dotnet test          # green 3/3
# then: add Directory.Packages.props, strip versions from Spike.csproj
dotnet test          # green 3/3 under CPM
```

## Spike.csproj (CPM form; explicit-version form uses the same numbers)
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Reqnroll" />
    <PackageReference Include="Reqnroll.xunit.v3" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
</Project>
```

## Directory.Packages.props
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
    <PackageVersion Include="Reqnroll" Version="3.3.4" />
    <PackageVersion Include="Reqnroll.xunit.v3" Version="3.3.4" />
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
  </ItemGroup>
</Project>
```

## reqnroll.json
```json
{ "$schema": "https://schemas.reqnroll.net/reqnroll-config-latest.json" }
```

## Spike.feature (3 scenarios simplified from docs/features/agent-memory/agent-memory.feature)
Rules FR-MEM-1.2 (project scoping), FR-MEM-1.8/1.9 (write/search/dedupe). Background:
`Given a project with id "acme-web" exists`. Scenarios: "Every tool requires a project id",
"Text written to the project is searchable", "Duplicate content is written once".

## Step-definition shape
- `[Binding]` class + `Reqnroll` / `Xunit` usings; regex step attributes as verbatim strings
  (e.g. `[When(@"I write ""(.*)"" to project ""(.*)""")]`).
- Trivial in-memory `Dictionary<string, List<string>>` store; `Assert.*` from xunit.v3.
- **Pitfall hit in practice:** a `static` store leaked state across scenarios and failed the
  "duplicate content is written once" scenario with leftover entries. Fix: `[BeforeScenario]`
  hook clearing the store / last error / last results. Not a Reqnroll defect — test-authoring bug.

## Result lines
```
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: ~40 ms - Spike.dll (net10.0)
```
(identical in both runs; duration excluded because it was noisy on first run)

## Generated artifacts (build-time codegen confirmed)
`obj/Debug/net10.0/` contained `Spike.feature.ndjson` and `SelfRegisteredExtensions.cs`;
the generated test class was `Spike.AgentMemorySpikeFeature` (visible in failure stack traces).
No committed `.feature.cs` needed.

## Supporting research (same session)
- NuGet flat-container API gave version lists: Reqnroll.xunit.v3 latest 3.3.4; core Reqnroll 3.3.4;
  official `Gherkin` parser package 42.0.0; SpecFlow.xUnit stale at betas.
- nuspec for Reqnroll.xunit.v3 3.3.4: deps Reqnroll 3.3.4, Reqnroll.Tools.MsBuild.Generation 3.3.4,
  xunit.v3.assert >= 2.0.0, xunit.v3.extensibility.core >= 2.0.0; targetFramework netstandard2.0.
- Docs (docs.reqnroll.net/latest/integrations/xunit.html): xUnit v3 supported since Reqnroll 3.1;
  needs xunit.runner.visualstudio >= 3.0.2; generated tests are async.
- SpecFlow EOL announcement: reqnroll.net/news/2025/01/specflow-end-of-life-has-been-announced/.
