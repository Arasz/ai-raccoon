using System.Text.Json;
using AiRaccoon.Hosting.Node;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     `IsAuthorized` in isolation: which envelope(s) the gate accepts for the one loopback
///     credential, and which 401 message a caller sees (docs/plans/2026-08-09-http-token-clients.md
///     §A, amendment R4/R8).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class McpTokenGateTests
{
    private const string Token = "the-real-token";
    private const string TokenPath = "/data-root/mcp-token";

    [Fact]
    public async Task Bearer_WithTheToken_Authorizes()
    {
        var (context, nextCalled) = await InvokeAsync(("Authorization", $"Bearer {Token}"));

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Bearer_LowercaseScheme_Authorizes()
    {
        var (_, nextCalled) = await InvokeAsync(("Authorization", $"bearer {Token}"));

        nextCalled.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Basic", "the-real-token")]
    [InlineData("Bearer", "wrong-token")]
    public async Task Authorization_WrongSchemeOrToken_Is401(string scheme, string token)
    {
        var (context, nextCalled) = await InvokeAsync(("Authorization", $"{scheme} {token}"));

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    /// <summary>The scheme parse's own edges: an empty remainder must never meet an empty secret.</summary>
    [Theory]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer  ")]
    public async Task Authorization_BearerWithNoToken_Is401(string headerValue)
    {
        var (context, nextCalled) = await InvokeAsync(("Authorization", headerValue));

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Authorization_BareTokenWithNoScheme_Is401()
    {
        var (_, nextCalled) = await InvokeAsync(("Authorization", Token));

        nextCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Authorization_TwoHeaderValues_Is401()
    {
        var context = NewMcpContext();
        context.Request.Headers.Append("Authorization", $"Bearer {Token}");
        context.Request.Headers.Append("Authorization", $"Bearer {Token}");
        var nextCalled = false;

        await BuildGate(ctx =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            })
            .InvokeAsync(context);

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task CustomHeader_Alone_StillAuthorizes()
    {
        var (_, nextCalled) = await InvokeAsync((McpTokenGate.HeaderName, Token));

        nextCalled.ShouldBeTrue();
    }

    /// <summary>R8: the natural `if (custom present) return compare(custom);` edit breaks this —
    /// a wrong custom header must not shadow a correct Bearer.</summary>
    [Fact]
    public async Task WrongCustomHeader_PlusCorrectBearer_Authorizes()
    {
        var (_, nextCalled) = await InvokeAsync(
            (McpTokenGate.HeaderName, "wrong"),
            ("Authorization", $"Bearer {Token}"));

        nextCalled.ShouldBeTrue();
    }

    /// <summary>R8's mirror: a wrong Bearer must not shadow a correct custom header.</summary>
    [Fact]
    public async Task WrongBearer_PlusCorrectCustomHeader_Authorizes()
    {
        var (_, nextCalled) = await InvokeAsync(
            (McpTokenGate.HeaderName, Token),
            ("Authorization", "Bearer wrong"));

        nextCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task NonMcpPath_IsUntouched()
    {
        var context = NewContext("/observability");
        var nextCalled = false;

        await BuildGate(ctx =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            })
            .InvokeAsync(context);

        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    /// <summary>R4: a present-but-wrong credential must read differently from an absent one, in
    /// either envelope, and both bodies still name the header and the token path.</summary>
    [Theory]
    [MemberData(nameof(MismatchCases))]
    public async Task PresentButWrongCredential_GetsAMismatchMessage_DifferentFromAbsent(
        string headerName, string headerValue)
    {
        var (absentContext, _) = await InvokeAsync();
        var (mismatchContext, nextCalled) = await InvokeAsync((headerName, headerValue));

        nextCalled.ShouldBeFalse();
        var absentBody = await ReadBodyAsync(absentContext);
        var mismatchBody = await ReadBodyAsync(mismatchContext);
        mismatchBody.ShouldNotBe(absentBody);
        mismatchBody.ShouldContain("does not match");
        mismatchBody.ShouldContain(TokenPath);
        mismatchBody.ShouldContain(McpTokenGate.HeaderName);
        absentBody.ShouldContain(TokenPath);
        absentBody.ShouldContain(McpTokenGate.HeaderName);
    }

    public static TheoryData<string, string> MismatchCases() =>
        new()
        {
            { McpTokenGate.HeaderName, "wrong" },
            { "Authorization", "Bearer wrong" }
        };

    [Fact]
    public async Task Absent_Is401AndCarriesJsonRpcAndTokenPath()
    {
        var (context, nextCalled) = await InvokeAsync();

        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        var body = await ReadBodyAsync(context);
        JsonDocument.Parse(body).RootElement.TryGetProperty("jsonrpc", out _).ShouldBeTrue();
        body.ShouldContain(TokenPath);
        body.ShouldContain(McpTokenGate.HeaderName);
    }

    private static async Task<(HttpContext Context, bool NextCalled)> InvokeAsync(
        params (string Name, string Value)[] headers)
    {
        var context = NewMcpContext();
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        var nextCalled = false;
        await BuildGate(ctx =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            })
            .InvokeAsync(context);

        return (context, nextCalled);
    }

    private static McpTokenGate BuildGate(RequestDelegate next) => new(next, Token, TokenPath);

    private static HttpContext NewMcpContext() => NewContext("/mcp");

    private static HttpContext NewContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
