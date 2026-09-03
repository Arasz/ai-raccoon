## 11. Adding tools to an existing MCP server

When the MCP server project already has tool classes (e.g. `SalaryTools`), adding new tools follows a tighter sequence:

1. **Read the spec/requirements** to understand REST endpoint mapping (method, path, body, query params).
2. **Study the existing tool** as a template — match its style, DTO patterns, and `EnsureSuccessStatusCode()` usage.
3. **Write tests first** (TDD RED) — create the test file with `MockHttpHandler`, write all test cases from the spec's test matrix. Run `dotnet test` to confirm they fail (compilation error or method-not-found).
4. **Write the implementation** — create the tool class, register in `Program.cs`.
5. **Build and test** (TDD GREEN) — `dotnet build && dotnet test`.
6. **Update docs** — `docs/functional-specification.md` §3 tool list + counts.

### Program.cs registration for a new tool class

```csharp
// Add typed HttpClient (same apiBaseUrl as existing tools)
builder.Services.AddHttpClient<SignalTools>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

// Chain WithTools<>() onto the existing registration
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RandomNumberTools>()
    .WithTools<SalaryTools>()
    .WithTools<SignalTools>();  // new
```

### POST with empty body

For tools that POST without a request body (e.g. confirming a proposal, triggering a check), use `PostAsync` with `null` content — not `PostAsJsonAsync`:

```csharp
var response = await httpClient.PostAsync($"/api/resource/{id}/action", null);
response.EnsureSuccessStatusCode();
return await response.Content.ReadAsStringAsync();
```

### Query parameter construction

For tools with optional filter parameters, build query strings from non-null args:

```csharp
var queryParams = new List<string>();
if (applicationId is { }) queryParams.Add($"applicationId={Uri.EscapeDataString(applicationId)}");
if (disposition is { }) queryParams.Add($"disposition={Uri.EscapeDataString(disposition)}");
if (source is { }) queryParams.Add($"source={Uri.EscapeDataString(source)}");
if (since is { }) queryParams.Add($"since={Uri.EscapeDataString(since)}");

var query = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";
var response = await httpClient.GetAsync($"/api/resource{query}");
```

Always use `Uri.EscapeDataString()` for values going into URLs — never raw string interpolation.

### Error handling test pattern

A single `[Theory]` with `[InlineData]` covers all non-success status codes for any tool:

```csharp
[Theory]
[InlineData(HttpStatusCode.NotFound)]
[InlineData(HttpStatusCode.Conflict)]
[InlineData(HttpStatusCode.BadRequest)]
public async Task AnyTool_ThrowsOnNonSuccessStatusCode(HttpStatusCode errorStatus)
{
    var (tools, _) = CreateTools(errorStatus);
    await Assert.ThrowsAsync<HttpRequestException>(() => tools.GetSignal("sig-x"));
}
```
