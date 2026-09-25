using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AiRaccoon.Core.Embedding;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Embedding.NeuralEngine;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.ML.OnnxRuntime;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     ADR-0118 "Switching devices", layer 2: the bundled engine built the way <see cref="EmbeddingService" />
///     builds it, one device after another in one process, each disposed before the next. Every step
///     must land on the provider it asked for, match the CPU vectors, and leave nothing behind for the
///     next one. The data root is shared by every order, so only the first CoreML step compiles.
///     macOS on Apple Silicon, with the MLX plugin and WebGPU, only.
/// </summary>
[Collection(NeuralEngineCollection.Name)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DeviceSwitchTests(DeviceSwitchTests.SharedDataRoot dataRoot, ITestOutputHelper output)
    : IClassFixture<DeviceSwitchTests.SharedDataRoot>
{
    private const int NeuralEngineServingEventId = 437;
    private const double ParityCosine = 0.999;
    private static readonly TimeSpan SettleWithin = TimeSpan.FromMinutes(6);

    private static readonly Dictionary<string, string> ExpectedProvider = new(StringComparer.Ordinal)
    {
        ["coreml"] = "CoreML",
        ["mlx"] = "MLX",
        ["gpu"] = "WebGPU",
        ["cpu"] = "CPU"
    };

    [RetryFact]
    public Task CoreMl_Mlx_Gpu_Cpu_CoreMl() => SwitchThroughAsync("coreml", "mlx", "gpu", "cpu", "coreml");

    [RetryFact]
    public Task Mlx_CoreMl_Mlx() => SwitchThroughAsync("mlx", "coreml", "mlx");

    [RetryFact]
    public Task Cpu_CoreMl_Gpu() => SwitchThroughAsync("cpu", "coreml", "gpu");

    internal static void SkipUnlessEveryDeviceCanRun()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Assert.Skip("switching through coreml and mlx runs on macOS on Apple Silicon only");
        }

        var directory = BundledModel.ResolveDirectory();
        if (OnnxEmbeddingGenerator.ResolveMlxPluginDirectory(AppContext.BaseDirectory) is null
            || OnnxEmbeddingGenerator.ResolveMlxGraphPath(Path.Combine(directory, "model_fp16.onnx")) is null)
        {
            Assert.Skip("the MLX plugin runtime is not next to this build; run scripts/download-mlx-runtime.py");
        }

        if (!OrtEnv.Instance().GetAvailableProviders().Contains("WebGpuExecutionProvider", StringComparer.Ordinal))
        {
            Assert.Skip("the loaded ONNX Runtime core has no WebGPU");
        }

        if (!File.Exists(Path.Combine(directory, CoreMlGraph.FileName)) || !File.Exists(Path.Combine(directory, CoreMlGraph.WeightsFileName)))
        {
            Assert.Skip("the ANE graph or the fp16 weights are not in the bundled model directory");
        }
    }

    /// <summary>The bundled generator under <paramref name="device" />, built by a fresh <see cref="EmbeddingService" /> over <paramref name="root" />.</summary>
    internal static (ILocalEmbeddingGenerator Generator, FakeLogger<EmbeddingService> Logger) Build(string device, string root)
    {
        var logger = new FakeLogger<EmbeddingService>();
        var store = new InMemorySettings();
        store.Values[EmbeddingSettingsKeys.Device] = device;
        var service = new EmbeddingService(logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            NoOpMeasurementRecorder.Instance, TimeProvider.System,
            new InfrastructureOptions { DataRoot = root, Scope = InstallScope.User }, store);
        var generator = service.CreateGenerator(new EmbeddingSettings("local", null, null, null)).ShouldBeAssignableTo<ILocalEmbeddingGenerator>()!;
        return (generator, logger);
    }

    internal static async Task<string> SettleAsync(NeuralEngineEmbeddingGenerator generator, FakeLogger<EmbeddingService> logger,
        CancellationToken ct)
    {
        (await generator.WaitUntilSettledAsync(ct).WaitAsync(SettleWithin, ct)).ShouldBe(NeuralEngineState.NeuralEngineServing,
            string.Join("; ", generator.Switch.History.Select(t => $"{t.Trigger}: {t.Reason}")));
        generator.Switch.History.ShouldNotContain(t => t.To == NeuralEngineState.Refused);
        return logger.Collector.GetSnapshot().Single(r => r.Id.Id == NeuralEngineServingEventId).Message;
    }

    /// <summary>Three fixed rows: short, about 300 tokens, about 1000 tokens.</summary>
    internal static string[] Rows()
    {
        var directory = BundledModel.ResolveDirectory();
        var tokenizer = new EmbeddingTokenizerFactory().Create(Descriptor(), directory);
        return ["Where does the drain embed pending rows?", TextOf(tokenizer, 300), TextOf(tokenizer, 1000)];
    }

    internal static async Task<GeneratedEmbeddings<Embedding<float>>> CpuReferenceAsync(string[] rows, CancellationToken ct)
    {
        var directory = BundledModel.ResolveDirectory();
        var descriptor = Descriptor();
        using var cpu = new OnnxEmbeddingGenerator(Path.Combine(directory, descriptor.OnnxModelFile),
            new EmbeddingTokenizerFactory().Create(descriptor, directory), descriptor, NullLogger.Instance);
        return await cpu.GenerateAsync(rows, cancellationToken: ct);
    }

    internal static double MinCosine(GeneratedEmbeddings<Embedding<float>> actual, GeneratedEmbeddings<Embedding<float>> expected) =>
        actual.Select((embedding, i) => TestData.Cosine(embedding.Vector, expected[i].Vector)).Min();

    private async Task SwitchThroughAsync(params string[] devices)
    {
        SkipUnlessEveryDeviceCanRun();
        var ct = TestContext.Current.CancellationToken;
        var rows = Rows();
        var reference = await CpuReferenceAsync(rows, ct);
        var ranMlx = false;

        foreach (var device in devices)
        {
            var clock = Stopwatch.StartNew();
            var (generator, logger) = Build(device, dataRoot.Path);
            var settled = generator is NeuralEngineEmbeddingGenerator neuralEngine
                ? $", {await SettleAsync(neuralEngine, logger, ct)}"
                : "";
            var seconds = clock.Elapsed.TotalSeconds;

            generator.ExecutionProvider.ShouldBe(ExpectedProvider[device], $"step {device}");
            var cosine = MinCosine(await generator.GenerateAsync(rows, cancellationToken: ct), reference);
            cosine.ShouldBeGreaterThanOrEqualTo(ParityCosine, $"step {device}");
            if (device != "mlx" && ranMlx && generator is OnnxEmbeddingGenerator notMlx)
            {
                notMlx.MlxCacheLimitApplied.ShouldBeFalse($"step {device}");
            }

            generator.Dispose();

            if (generator is NeuralEngineEmbeddingGenerator disposed)
            {
                disposed.Compile.IsCompleted.ShouldBeTrue($"step {device}: the CoreML compile outlived dispose");
            }

            if (generator is OnnxEmbeddingGenerator onnx)
            {
                onnx.MlxThreadAlive.ShouldBeFalse($"step {device}: the MLX thread outlived dispose");
            }

            OnnxEmbeddingGenerator.GpuGateIsFree().ShouldBeTrue($"step {device}: GpuGate is still held");
            ranMlx |= device == "mlx";
            output.WriteLine($"{device}: provider {generator.ExecutionProvider}, cosine min {cosine:F6}, ready in {seconds:F1} s{settled}");
        }
    }

    private static EngineDescriptor Descriptor() =>
        new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())
            .Load(BundledModel.ResolveDirectory());

    private static string TextOf(IEmbeddingTokenizer tokenizer, int tokens)
    {
        const string sentence = "Memory rows are chunked, embedded and searched by meaning across every project.";
        var text = new StringBuilder(sentence);
        while (tokenizer.EncodeToIds(text.ToString(), true).Count < tokens)
        {
            text.Append(' ').Append(sentence);
        }

        return text.ToString();
    }

    /// <summary>One data root, and so one CoreML cache, for every order in the class.</summary>
    public sealed class SharedDataRoot : IDisposable
    {
        public string Path { get; } = TestData.CreateTempRoot("device-switch");

        public void Dispose() => TestData.DeleteTempRoot(Path);
    }
}
