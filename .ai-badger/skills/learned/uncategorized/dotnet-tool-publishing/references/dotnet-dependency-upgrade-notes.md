# Upgrading a .NET tool's dependencies to current majors (2026-08, dotnet-ignore)

Session knowledge bank: net8.0 -> net10.0, xunit 2 -> v3, Octokit 9 -> 14,
FluentAssertions 6 -> 8, Microsoft.NET.Test.Sdk 17 -> 18, coverlet.collector 10.

## Finding latest stable versions (NuGet flatcontainer)

GET `https://api.nuget.org/v3-flatcontainer/{pkg-lowercase}/index.json` ->
`versions[]`; drop prereleases (version contains `-`); last remaining is the
latest stable. Batch many packages in one python loop. (Fuller recipe:
`dotnet-cli-parsing`/references/nuget-evidence-research.md.)

## Probing a NuGet package's API surface without docs (dotnet fsi)

XML docs in the package omit ctor signatures; guessing costs compile-error
round-trips. Probe the real assembly:

```fsharp
#r "nuget: Octokit, 14.0.0"
open System
open Octokit
typeof<RepositoryContent>.GetConstructors()
|> Array.iter (fun c -> c.GetParameters()
    |> Array.map (fun p -> p.ParameterType.Name + " " + p.Name)
    |> String.concat ", " |> printfn "ctor: %s")
```

`dotnet fsi probe.fsx` prints the exact ctor/param list. Gotcha: property
`CanWrite` is true even for PRIVATE setters — an object initializer then fails
with CS0272 at compile time, so check `GetSetMethod()` accessibility before
using `new T { ... }`.

## Octokit 9 -> 14 (the dotnet-sdk rewrite) deltas

- `IGitHubClient.Repository` is `IRepositoriesClient` (NOT `IRepositoryClient` —
  that type doesn't exist; CS0246).
- `RepositoryContent`: parameterless ctor exists but ALL setters are private
  (object initializer -> CS0272); use the 13-param ctor
  (name, path, sha, size, type, downloadUrl, url, gitUrl, htmlUrl, encoding,
  encodedContent, target, submoduleGitUrl).
- `RepositoryContent.Type` is `StringEnum<ContentType>` — `content.Type == ContentType.File`
  compiles via implicit conversion (the old `content.Type.Value == ...` also
  still compiles).
- `GetAllContents(owner, repo)` overload exists (also (owner, repo, path),
  (repoId, path), (repoId)).
- Replace old live-API integration tests with mocks: `Mock<IGitHubClient>` ->
  `Mock<IRepositoriesClient>` -> `Mock<IRepositoryContentsClient>` plus a mocked
  `HttpMessageHandler` (Moq `Protected().Setup<Task<HttpResponseMessage>>("SendAsync", ...)`).
- Unauthenticated GitHub API limit is 60 req/hr — cache the `GetAllContents`
  listing per run (one call, not one per requested name); downloads go through
  `DownloadUrl` (raw.githubusercontent.com, not rate-limited the same way). The
  limit WILL bite mid-session when smoke tests keep hitting the API; mocked unit
  tests are the authoritative gate then.

## xunit 2 -> xunit v3 deltas

- Packages: `xunit.v3` (meta; core+assert+analyzers) + `xunit.runner.visualstudio`
  3.x + `Microsoft.NET.Test.Sdk` (17.12+; 18.8.1 verified 2026-08). VSTest mode
  keeps the test project a library (no `OutputType=Exe` — that is
  Microsoft.Testing.Platform mode).
- Remove `<DotNetCliToolReference Include="dotnet-xunit"/>` (obsolete).
- Test project needs `<Nullable>enable</Nullable>` before `T?` annotations compile
  (CS8632) — existing `X x = null;` lines then need `X?` (CS8600).
- xUnit1051 analyzer: any call accepting a CancellationToken should pass
  `TestContext.Current.CancellationToken` (xunit v3's TestContext).
- A test class named `FooTest` (singular) is NOT matched by
  `--filter FullyQualifiedName~FooTests` — filter by the actual class name.
- xunit v3 VSTest mode + coverlet.collector 10.x: `dotnet test --collect:"XPlat Code Coverage"`
  works unchanged.

## macOS case-only git mv

`git mv FIles Files` fails "Invalid argument" on case-insensitive APFS — rename in
two steps via a temp name: `git mv FIles tmp && git mv tmp Files`.
