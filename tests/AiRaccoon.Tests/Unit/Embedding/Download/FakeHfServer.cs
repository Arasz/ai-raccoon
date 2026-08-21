using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Infrastructure.Embedding.Download;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding.Download;

/// <summary>
///     WP2 (D4/D8): tree resolution against a local fake HF API — lfs.oid pins captured with
///     expand=true, pagination past the 1000-entry limit via the Link header, actionable errors
///     on missing repos.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class HfTreeClientTests : IDisposable
{
    private readonly FakeHfServer _server = FakeHfServer.Start();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ResolvesTree_WithLfsOidsAndSizes()
    {
        _server.Tree("BAAI/bge-m3", "main",
            """
            [
              { "path": "onnx/model.onnx", "type": "file", "size": 724923, "lfs": { "oid": "f84251230831afb359ab26d9fd37d5936d4d9bb5d1d5410e66442f630f24435b" } },
              { "path": "onnx/config.json", "type": "file", "size": 698, "lfs": null },
              { "path": "onnx", "type": "directory" }
            ]
            """);

        var tree = await Client().GetTreeAsync("BAAI/bge-m3", "main", TestContext.Current.CancellationToken);

        tree.Count.ShouldBe(3);
        tree[0].ShouldBe(new HfTreeEntry("onnx/model.onnx", "file", 724923,
            "f84251230831afb359ab26d9fd37d5936d4d9bb5d1d5410e66442f630f24435b"));
        tree[1].LfsOid.ShouldBeNull();
        tree[2].Type.ShouldBe("directory");
    }

    [Fact]
    public async Task PaginatesPastOneThousandEntries_FollowingTheLinkHeader()
    {
        var page1 = string.Join(",", Enumerable.Range(0, 1000)
            .Select(i => $$"""{"path": "f{{i:D4}}.bin", "type": "file", "size": 10, "lfs": null}"""));
        var next = $"{_server.BaseUrl}/api/models/big/repo/tree/main?recursive=true&expand=true&limit=1000&page=2";
        // RFC 5988: <url>; rel="next". Register the second page first: route order is checked
        // top-down, and the page-1 route matches every query. The client must follow the Link
        // header, not re-request page 1.
        _server.Tree("big/repo", "main",
            """
            [
              { "path": "tail-a.bin", "type": "file", "size": 10, "lfs": null },
              { "path": "tail-b.bin", "type": "file", "size": 10, "lfs": null },
              { "path": "tail-c.bin", "type": "file", "size": 10, "lfs": null }
            ]
            """,
            querySuffix: "&page=2");
        _server.Tree("big/repo", "main", $"[{page1}]", nextLink: $"<{next}>; rel=\"next\"");

        var tree = await Client().GetTreeAsync("big/repo", "main", TestContext.Current.CancellationToken);

        _server.Hits.Count(h => h.Key.Contains("/tree/", StringComparison.Ordinal))
            .ShouldBe(2, "the Link header must be followed: " + string.Join(";", _server.Hits.Select(h => $"{h.Key}={h.Value}")));
        tree.Count.ShouldBe(1003);
        tree[^1].Path.ShouldBe("tail-c.bin");
    }

    [Fact]
    public async Task MissingRepoOrRevision_Throws_WithActionableMessage()
    {
        _server.Tree("nope/missing", "main", "[]", status: 404);

        var ex = await Should.ThrowAsync<HfApiException>(
            () => Client().GetTreeAsync("nope/missing", "main", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("nope/missing");
        ex.Message.ShouldContain("main");
        ex.Message.ShouldContain("404");
    }

    [Fact]
    public async Task NonSuccessStatus_Throws_WithActionableMessage()
    {
        _server.Tree("slow/repo", "main", "[]", status: 503);

        var ex = await Should.ThrowAsync<HfApiException>(
            () => Client().GetTreeAsync("slow/repo", "main", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("503");
    }

    private HfTreeClient Client() => new(new HttpClient(), _server.BaseUrl);
}

/// <summary>Minimal canned-response HTTP server on 127.0.0.1 for the mocked-HF fixture tests.</summary>
internal sealed class FakeHfServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<(string Prefix, Func<string, FakeHfResponse?> Handler)> _routes = [];
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    private FakeHfServer(int port)
    {
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl { get; }

    /// <summary>Request count keyed by absolute path (tree and resolve URLs share no path space).</summary>
    public IReadOnlyDictionary<string, int> Hits => _hits;

    public static FakeHfServer Start()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return new FakeHfServer(port);
    }

    /// <summary>Serves a tree listing; <paramref name="querySuffix" /> matches the request's raw query.</summary>
    public void Tree(string repo, string revision, string json, string? nextLink = null, string? querySuffix = null, int status = 200)
    {
        var prefix = $"/api/models/{repo}/tree/{revision}";
        On(prefix, query =>
        {
            if (querySuffix is not null && !query.Contains(querySuffix, StringComparison.Ordinal))
            {
                return null;
            }

            return new FakeHfResponse(status, Encoding.UTF8.GetBytes(json), nextLink);
        });
    }

    /// <summary>Serves file bytes at /{repo}/resolve/{revision}/{path}; the revision segment is wildcarded.</summary>
    public void Resolve(string repo, string path, byte[] bytes, int status = 200)
    {
        var prefix = $"/{repo}/resolve/";
        On(prefix, rest => rest.EndsWith("/" + path, StringComparison.Ordinal)
            ? new FakeHfResponse(status, bytes, null)
            : null);
    }

    /// <summary>Serves text content at /{repo}/raw/{revision}/{path} (config.json etc.).</summary>
    public void Raw(string repo, string path, string content, int status = 200)
    {
        var prefix = $"/{repo}/raw/";
        On(prefix, rest => rest.EndsWith("/" + path, StringComparison.Ordinal)
            ? new FakeHfResponse(status, Encoding.UTF8.GetBytes(content), null)
            : null);
    }

    public void On(string prefix, Func<string, FakeHfResponse?> handler)
    {
        lock (_routes)
        {
            _routes.Add((prefix, handler));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            lock (_hits)
            {
                _hits[path + context.Request.Url?.Query] = _hits.GetValueOrDefault(path + context.Request.Url?.Query) + 1;
            }

            FakeHfResponse? response;
            lock (_routes)
            {
                response = _routes
                    .Where(r => path.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Handler(path[r.Prefix.Length..] + context.Request.Url?.Query))
                    .FirstOrDefault(r => r is not null);
            }

            if (response is null)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            context.Response.StatusCode = response.Status;
            if (response.LinkHeader is not null)
            {
                context.Response.AddHeader("Link", response.LinkHeader);
            }

            if (response.Body is { Length: > 0 })
            {
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = response.Body.Length;
                await context.Response.OutputStream.WriteAsync(response.Body);
            }

            context.Response.Close();
        }
        catch (Exception)
        {
            try
            {
                context.Response.Abort();
            }
            catch (Exception)
            {
                // listener already gone
            }
        }
    }
}

internal sealed record FakeHfResponse(int Status, byte[]? Body, string? LinkHeader)
{
    public static FakeHfResponse Json(string json, string? linkHeader = null) => new(200, Encoding.UTF8.GetBytes(json), linkHeader);
}
