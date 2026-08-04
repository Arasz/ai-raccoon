using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>One embeddings request captured by the fake endpoint.</summary>
public sealed record CapturedEmbeddingRequest(string Model, IReadOnlyList<string> Inputs, string? Authorization);

/// <summary>
///     In-process fake of an OpenAI-compatible /embeddings endpoint (FR-NM-3 scenario 3):
///     returns deterministic 384-dim vectors derived from the input text so tests can assert
///     routing and re-embedding without any external service.
/// </summary>
public sealed class FakeEmbeddingEndpoint : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeEmbeddingEndpoint(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; private set; }

    public List<CapturedEmbeddingRequest> Requests { get; } = [];

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    /// <summary>Deterministic vector for a text: distinct per input, stable across calls.</summary>
    public static float[] VectorFor(string text, int index = 0)
    {
        var hash = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);
        var vector = new float[384];
        for (var d = 0; d < vector.Length; d++)
        {
            vector[d] = (uint)(hash + d * 31 + index * 7) % 1000 / 1000f;
        }

        return vector;
    }

    public static async Task<FakeEmbeddingEndpoint> StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var endpoint = new FakeEmbeddingEndpoint(app, "");

        app.MapPost("/v1/embeddings", async (HttpContext context) =>
        {
            using var body = new StreamReader(context.Request.Body);
            using var document = JsonDocument.Parse(await body.ReadToEndAsync());
            var root = document.RootElement;
            var model = root.GetProperty("model").GetString() ?? "";
            var inputs = new List<string>();
            if (root.TryGetProperty("input", out var input))
            {
                if (input.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in input.EnumerateArray())
                    {
                        inputs.Add(item.GetString() ?? "");
                    }
                }
                else
                {
                    inputs.Add(input.GetString() ?? "");
                }
            }

            // Recorded for assertions (routing, model id, auth header).
            endpoint.Requests.Add(new CapturedEmbeddingRequest(model, inputs,
                context.Request.Headers.Authorization.ToString()));

            var data = new List<object>();
            for (var i = 0; i < inputs.Count; i++)
            {
                data.Add(new { @object = "embedding", index = i, embedding = VectorFor(inputs[i], i) });
            }

            var response = new
            {
                @object = "list",
                data,
                model,
                usage = new { prompt_tokens = 1, total_tokens = 1 }
            };

            return Results.Json(response);
        });

        await app.StartAsync(cancellationToken);
        endpoint.BaseUrl = $"{app.Urls.First()}/v1";
        return endpoint;
    }
}
