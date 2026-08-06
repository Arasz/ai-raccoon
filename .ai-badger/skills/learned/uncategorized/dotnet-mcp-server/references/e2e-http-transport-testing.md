# E2E-testing an MCP server over the HTTP transport (WebApplicationFactory + MCP client)

Verified against ModelContextProtocol.Core 2.0.0 and xunit.v3 3.2.2. This is the recipe that
proved a real startup DI bug ("Unable to resolve service for type 'SyncOptions'") that the
tool-class unit tests could not catch.

## Packages (test project)

- `Microsoft.AspNetCore.Mvc.Testing` (10.0.10 matched the app's other 10.0.x packages)
- `ModelContextProtocol.Core` (2.0.0) — the CLIENT lives here, not in the `ModelContextProtocol`
  server package. `McpClientFactory` from older blog posts is a different package/era; use
  `McpClient.CreateAsync(transport)`.
- `<FrameworkReference Include="Microsoft.AspNetCore.App"/>` in the test csproj (WebApplicationFactory
  needs the shared framework).
- With Central Package Management: test-scope packages go in the TEST project's own
  `tests/Directory.Packages.props` if one exists (a root + tests split is common) — NU1010
  "PackageReference items do not define a corresponding PackageVersion" means you added the
  reference in the wrong props file. Also: a configured local feed that doesn't exist yet
  (`NU1301: local source ... doesn't exist`) is fixed by `mkdir`-ing it.

## Factory shape

```csharp
public sealed class McpServerFactory : WebApplicationFactory<Program>
{
    private readonly string _dataRoot = CreateTempRoot();
    private readonly string? _previousTransport;
    private readonly string? _previousDataRoot;

    public McpServerFactory()
    {
        // The server reads these env vars in Program.cs top-level code BEFORE the host builds,
        // so ConfigureWebHost/ConfigureAppConfiguration are too late. Mutate the real process env.
        _previousTransport = Environment.GetEnvironmentVariable("MCP_TRANSPORT");
        _previousDataRoot = Environment.GetEnvironmentVariable("AIRACCOON_DATA_ROOT");
        Environment.SetEnvironmentVariable("MCP_TRANSPORT", "http");
        Environment.SetEnvironmentVariable("AIRACCOON_DATA_ROOT", _dataRoot);
        TryProvisionNativeExtensions();
    }

    public async Task<McpClient> CreateClientAsync()
    {
        var httpClient = CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "e2e-test",
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,   // NOT "Streamable"
            },
            httpClient,
            LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)),
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("MCP_TRANSPORT", _previousTransport);
        Environment.SetEnvironmentVariable("AIRACCOON_DATA_ROOT", _previousDataRoot);
        // best-effort Directory.Delete(_dataRoot, recursive: true)
    }
}
```

## Serial collection (mandatory)

Env mutation is process-global — parallel test classes would clobber each other's transport/data
root. Put every E2E class in one non-parallel collection:

```csharp
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2ETestCollection { public const string Name = "E2E"; }

[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public class McpServerE2ETests : IAsyncLifetime { ... }
```

## Traits (filterable test tiers)

Two trait dimensions let CI run the fast unit slice separately from the slow stack:
`[Trait("Category", "Unit"|"Integration"|"E2E")]` + `[Trait("Speed", "Fast"|"Slow")]`.
Filter: `dotnet test --filter "Category=Unit&Speed=Fast"`. Rule of thumb: pure logic/fakes = Unit,
real SQLite or native extensions = Integration, full server over HTTP = E2E; Fast = Unit, Slow =
everything else.

## Native-extension servers: provision or skip honestly

A server backed by native sqlite extensions can't be faked. Copy the host RID's already-provisioned
modules (`~/.ai-raccoon/extensions/<rid>`) into the temp data root, and in the test fixture:

```csharp
if (!_factory.HasNativeExtensions)
    Assert.Skip("native extensions not provisioned for this host RID; skipping E2E tests");
```

`Assert.Skip` (xunit.v3) reports SKIP, never a false green.

## Client call shapes

```csharp
var result = await client.CallToolAsync("memory_write",
    new Dictionary<string, object?> { ["projectId"] = "acme", ["content"] = "..." },
    progress: null, options: null, CancellationToken.None);
```

- **`CallToolResult.IsError` is NULL on success** (the protocol omits `isError` unless true).
  Assert `result.IsError.ShouldNotBe(true)`, never `ShouldBe(false)` — Shouldly's `bool?`
  overloads reject `ShouldBeFalse()` with CS1929.
- Success content: `string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text))`
  gives the JSON text for assertions.
- Tool args are snake_case strings; tool results are JSON text — parse with `JsonDocument`.

## Assertion strategy when embeddings need a model

If search requires a configured embedding model and the test env has none, don't assert via
search. Prove the same behavior model-free:
- save → assert `memory_stats` shows `"entries":1` and the committed context name
- isolation → assert `memory_workspace_status` sees the write AND `memory_stats` still shows
  `"entries":0` (workspace scratch is not committed)
- consolidate → assert `"promoted":N` + stats entries count + post-status `"count":0`
- share → assert `"shared"` in the result + stats lists `shared`
- sync without credentials → `result.IsError.ShouldBe(true)` (the tool maps to an MCP error)
- real embeddings → gate on `AIRACCOON_TEST_GGUF` env; `Assert.Skip` when unset

## What E2E catches that unit tests cannot

- **Unregistered ctor dependency**: `AddSingleton<SyncService>()` whose ctor takes `SyncOptions`
  while only the containing `InfrastructureOptions` is registered fails at `builder.Build()`
  (ValidateOnBuild) with "Unable to resolve service for type 'X' while attempting to activate 'Y'".
  Fix: register the inner options object too (`services.AddSingleton(options.Sync)`).
- Wrong field name in the server after a refactor (ctor param vs stored field) — compiles in the
  src project, fails only when the tool actually runs.
