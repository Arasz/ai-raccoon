using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Benchmarks.SearchFixture;

/// <summary>
///     In-process fake of an OpenAI-compatible /embeddings endpoint: returns deterministic,
///     unit-normalized 384-dim vectors derived from the input text, so <see cref="SearchFixtureBank" />
///     can drive the store's real write/embed/search surface without ONNX or network cost
///     (docs/plans/2026-08-08-search-knn-perf.md WP4).
/// </summary>
public sealed class FakeEmbeddingEndpoint : IAsyncDisposable
{
    public const int Dimensions = 384;

    private readonly WebApplication _app;

    private FakeEmbeddingEndpoint(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    /// <summary>Deterministic unit vector for a text: distinct per input, stable across processes.</summary>
    public static float[] VectorFor(string text)
    {
        var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var random = new Random(seed);
        var vector = new float[Dimensions];
        for (var d = 0; d < Dimensions; d++)
        {
            vector[d] = (float)(random.NextDouble() * 2 - 1);
        }

        var lengthSquared = 0.0;
        foreach (var v in vector)
        {
            lengthSquared += (double)v * v;
        }

        var norm = MathF.Sqrt((float)lengthSquared);
        if (norm <= 0f)
        {
            return vector;
        }

        for (var d = 0; d < Dimensions; d++)
        {
            vector[d] /= norm;
        }

        return vector;
    }

    public static async Task<FakeEmbeddingEndpoint> StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.MapPost("/v1/embeddings", async context =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
            var root = document.RootElement;
            var model = root.TryGetProperty("model", out var modelProperty) ? modelProperty.GetString() ?? "" : "";
            var inputs = ReadInputs(root);

            var data = inputs.Select((text, index) => new { @object = "embedding", index, embedding = VectorFor(text) });
            await context.Response.WriteAsJsonAsync(
                    new { @object = "list", data, model, usage = new { prompt_tokens = 1, total_tokens = 1 } },
                    cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return new FakeEmbeddingEndpoint(app, $"{app.Urls.First()}/v1");
    }

    private static List<string> ReadInputs(JsonElement root)
    {
        var inputs = new List<string>();
        if (!root.TryGetProperty("input", out var input))
        {
            return inputs;
        }

        if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in input.EnumerateArray())
            {
                inputs.Add(item.GetString() ?? "");
            }

            return inputs;
        }

        inputs.Add(input.GetString() ?? "");
        return inputs;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);
}
