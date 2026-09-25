using System.Runtime.InteropServices;
using AiRaccoon.Core.Embedding;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     IEmbeddingGenerator over an ONNX embedding model (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature): one session run per text, then the
///     manifest-selected pooling (mean | cls | model-output) and normalization (l2 | none). The
///     bundled all-MiniLM-L6-v2 path — wordpiece tokenizer, mean-pool + L2, 256 window — is the
///     default descriptor and is behavior-preserved (G3 golden vectors).
/// </summary>
internal sealed partial class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>
    ///     Real-content token budget of the BUNDLED engine: the 256-token window minus the
    ///     [CLS]/[SEP] special tokens <see cref="Encode" /> adds via <c>addSpecialTokens: true</c> —
    ///     a chunk tokenizing to exactly this many WordPiece tokens fills the window without ever
    ///     reaching the truncation branch below (docs/adr/0036). Manifest engines resolve their own
    ///     budget through <see cref="EmbeddingService" /> (plan D6).
    /// </summary>
    public const int MaxContentTokens = 254;

    private const string KeyValueCachePrefix = "past_key_values.";

    private readonly ILogger _logger;
    private readonly InferenceSession _session;
    private readonly IEmbeddingTokenizer _tokenizer;
    private readonly int _window;
    private readonly string _pooling;
    private readonly string _normalization;
    private readonly IReadOnlyList<string> _inputNames;
    private readonly string _outputName;

    /// <summary>
    ///     Output dimension reported by the ONNX session itself (engineer doc §4.2.1) — the
    ///     replacement for the deleted <c>EmbeddingMath.Dimension</c> const. Manifest-declared
    ///     dimensions are cross-checked against this at construction.
    /// </summary>
    public int Dimension { get; }

    /// <summary>ORT intra-op threads this session was built with (WP11-A/G16); 0 means ORT's own default.</summary>
    public int IntraOpThreads { get; }

    /// <summary>The execution provider the session runs on: "WebGPU", "CPU", "MLX", "CUDA", or one of those
    /// with a parenthesized "(… refused: …)" suffix per fallback step actually taken.</summary>
    public string ExecutionProvider { get; private set; } = CpuProvider;

    private const string CpuProvider = "CPU";
    private const string WebGpuProvider = "WebGPU";
    private const string MlxProvider = "MLX";
    private const string CudaProvider = "CUDA";

    /// <summary>WebGPU sessions share one process-wide GPU context, which concurrent runs corrupt.</summary>
    private static readonly Lock GpuGate = new();

    /// <summary>True only for a session that actually landed on WebGPU (not a "(… refused: …)" fallback) — <see cref="Run" />'s gate check.</summary>
    private readonly bool _needsGpuGateForRun;

    /// <summary>Non-null only for an MLX session: the plugin is thread-affine (a session may run
    /// only on the exact OS thread it first ran on), so every call this generator makes into it —
    /// construction, Run, Dispose — is pinned to one dedicated thread (ADR-0110).</summary>
    private SingleThreadExecutor? _mlxExecutor;

    /// <summary>True for an MLX session: rows pad to <see cref="LengthBuckets" /> so MLX compiles and
    /// caches a handful of shapes instead of one per row length (ADR-0114).</summary>
    private bool _bucketRows;

    /// <summary>True when this MLX session capped MLX's free-buffer cache (ADR-0114).</summary>
    public bool MlxCacheLimitApplied { get; private set; }

    /// <summary>Sequence length of the most recent session run, padding included.</summary>
    internal int LastSequenceLength { get; private set; }

    /// <summary>Guards <see cref="Dispose" /> against being called twice — the standard IDisposable
    /// contract — since <see cref="_mlxExecutor" /> is itself single-use and throws on a second Run.</summary>
    private bool _disposed;

    /// <summary>What <see cref="Dispose" /> runs on the MLX thread to release <see cref="_session" />;
    /// a field (not a direct call) only so a test can substitute a throwing action and prove the
    /// executor is still disposed on that path. Always the real session's Dispose in production.</summary>
    private Action _mlxSessionDisposeAction;

    internal OnnxEmbeddingGenerator(string modelPath, IEmbeddingTokenizer tokenizer, EngineDescriptor descriptor, ILogger logger,
        int intraOpThreads = 0, bool preferGpu = false, bool preferMlx = false, string? cudaLibraryPath = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _logger = logger;
        _tokenizer = tokenizer;
        _window = descriptor.ContextWindowTokens;
        _pooling = descriptor.Pooling;
        _normalization = descriptor.Normalization;
        _inputNames = descriptor.InputNames;
        IntraOpThreads = intraOpThreads;

        string? mlxRefusalReason = null;
        string? cudaRefusalReason = null;
        var mlxSession = preferMlx ? CreateMlxSessionOrNull(modelPath, intraOpThreads, out mlxRefusalReason) : null;
        var cudaSession = mlxSession is null && cudaLibraryPath is not null
            ? CreateCudaSessionOrNull(modelPath, intraOpThreads, cudaLibraryPath, out cudaRefusalReason)
            : null;
        if (mlxSession is not null)
        {
            _session = mlxSession;
            ExecutionProvider = MlxProvider;
        }
        else if (cudaSession is not null)
        {
            _session = cudaSession;
            ExecutionProvider = CudaProvider;
        }
        else
        {
            _session = (preferGpu ? CreateGpuSessionOrNull(modelPath, intraOpThreads) : null)
                       ?? CreateCpuSession(modelPath, intraOpThreads);
            _needsGpuGateForRun = ExecutionProvider == WebGpuProvider;
            if (mlxRefusalReason is not null)
            {
                ExecutionProvider = $"{ExecutionProvider} (MLX refused: {mlxRefusalReason})";
            }

            if (cudaRefusalReason is not null)
            {
                ExecutionProvider = $"{ExecutionProvider} (CUDA refused: {cudaRefusalReason})";
            }
        }

        _mlxSessionDisposeAction = () => _session.Dispose();

        ValidateInputNames(descriptor);
        if (_pooling == "model-output" && string.IsNullOrWhiteSpace(descriptor.EmbeddingOutput))
        {
            throw new InvalidOperationException(
                $"Pooling mode 'model-output' requires an onnx.embeddingOutput (manifest model '{descriptor.Model}').");
        }

        _outputName = OutputNameFor(descriptor);
        Dimension = ReadOutputDimension(_session, _outputName, descriptor.Model);
        if (descriptor.Dimensions != Dimension)
        {
            throw new InvalidOperationException(
                $"Manifest model '{descriptor.Model}' declares {descriptor.Dimensions} dimensions but the ONNX session reports " +
                $"{Dimension} for output '{_outputName}'.");
        }

        // Adapting to the graph's rank (see Pool) is deliberate, but never silent: a manifest whose
        // pooling mode the graph makes unreachable is still a manifest to correct.
        if (_pooling != "model-output" && _session.OutputRank(_outputName) == OnnxOutputRanks.PooledRank)
        {
            Log.GraphPoolsItsOwnOutput(_logger, descriptor.Model, _pooling, _outputName);
        }
    }

    /// <summary>
    ///     Builds the same BERT WordPiece tokenizer the bundled engine embeds with, so a caller that
    ///     needs to *count* tokens the way this generator will (e.g. the chunker, for a guaranteed
    ///     budget — docs/adr/0036) uses an identically configured tokenizer rather than a
    ///     hand-duplicated copy of these options.
    /// </summary>
    public static BertTokenizer CreateTokenizer(string vocabPath) =>
        BertTokenizer.Create(vocabPath, new BertOptions
        {
            LowerCaseBeforeTokenization = true,
            ApplyBasicTokenization = true,
            SplitOnSpecialTokens = true,
            IndividuallyTokenizeCjk = true,
            RemoveNonSpacingMarks = true
        });

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.Select(Encode).ToList();
        var embeddings = new GeneratedEmbeddings<Embedding<float>>(items.Count);
        if (items.Count == 0)
        {
            return Task.FromResult(embeddings);
        }

        return Task.Run(() => RunEachRow(items, embeddings, cancellationToken), cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_mlxExecutor is { } executor)
        {
            try
            {
                executor.Run(() =>
                {
                    _mlxSessionDisposeAction();
                    return true;
                });
            }
            finally
            {
                executor.Dispose();
            }

            return;
        }

        _session.Dispose();
    }

    /// <summary>Attaches an executor to a normally-constructed (CPU) generator so Dispose's MLX
    /// branch is exercisable without a real onnxruntime MLX plugin. Test seam.</summary>
    internal void AttachMlxExecutorForTesting(SingleThreadExecutor executor)
    {
        _mlxExecutor = executor;
        _bucketRows = true;
    }

    /// <summary>Substitutes what Dispose runs on the MLX thread instead of the real session's
    /// Dispose, so a throwing disposal can be proven not to leak the executor. Test seam.</summary>
    internal void SetMlxSessionDisposeActionForTesting(Action action) => _mlxSessionDisposeAction = action;

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(List<NamedOnnxValue> feed)
    {
        if (_mlxExecutor is { } executor)
        {
            return executor.Run(() => _session.Run(feed));
        }

        if (!_needsGpuGateForRun)
        {
            return _session.Run(feed);
        }

        lock (GpuGate)
        {
            return _session.Run(feed);
        }
    }

    /// <summary>The standard ORT build implements WebGPU only on macOS (ADR-0108); elsewhere it comes from the plugin.</summary>
    private static bool BuiltInWebGpuAvailable() =>
        OrtEnv.Instance().GetAvailableProviders().Contains(WebGpuExecutionProviderName, StringComparer.Ordinal);

    private static InferenceSession CreateCpuSession(string modelPath, int intraOpThreads)
    {
        using var options = new SessionOptions();
        if (intraOpThreads > 0)
        {
            options.IntraOpNumThreads = intraOpThreads;
        }

        return new InferenceSession(modelPath, options);
    }

    /// <summary>GPU/MLX intra-op workers spin-wait between kernels by default, which burns CPU on a
    /// WebGPU or MLX session for no latency benefit; CPU-only sessions keep spinning because it helps
    /// CPU-bound runs. Applied to <see cref="CreateGpuSessionOrNull" />'s and
    /// <see cref="CreateMlxSessionOnCurrentThread" />'s options (ADR-0110).</summary>
    internal static readonly IReadOnlyDictionary<string, string> GpuSessionConfigEntries =
        new Dictionary<string, string> { ["session.intra_op.allow_spinning"] = "0" };

    /// <summary>A WebGPU session — ORT's built-in provider on macOS, the plugin on Windows and Linux — or
    /// null, the caller then building a CPU session; <see cref="ExecutionProvider" /> records any refusal.</summary>
    private InferenceSession? CreateGpuSessionOrNull(string modelPath, int intraOpThreads)
    {
        if (OperatingSystem.IsMacOS())
        {
            return BuiltInWebGpuAvailable() ? CreateBuiltInWebGpuSessionOrNull(modelPath, intraOpThreads) : null;
        }

        return OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
            ? CreatePluginWebGpuSessionOrNull(modelPath, intraOpThreads)
            : null;
    }

    /// <summary>A session with the built-in WebGPU provider appended (ORT keeps what the GPU cannot run
    /// on the CPU), or null when ORT refuses it.</summary>
    private InferenceSession? CreateBuiltInWebGpuSessionOrNull(string modelPath, int intraOpThreads)
    {
        try
        {
            using var options = new SessionOptions();
            if (intraOpThreads > 0)
            {
                options.IntraOpNumThreads = intraOpThreads;
            }

            foreach (var (key, value) in GpuSessionConfigEntries)
            {
                options.AddSessionConfigEntry(key, value);
            }

            options.AppendExecutionProvider(WebGpuProvider, new Dictionary<string, string>());
            InferenceSession session;
            lock (GpuGate)
            {
                session = new InferenceSession(modelPath, options);
            }

            ExecutionProvider = WebGpuProvider;
            return session;
        }
        catch (OnnxRuntimeException ex)
        {
            ExecutionProvider = $"{CpuProvider} (GPU refused: {ex.Message})";
            return null;
        }
    }

    private const string WebGpuExecutionProviderName = "WebGpuExecutionProvider";
    private const string WebGpuPluginDirectoryName = "webgpu";

    /// <summary>Why the WebGPU plugin is never tried: it registers and builds a session, then aborts the
    /// whole process on the first run, which no catch can turn into a fallback. Null re-enables it.</summary>
    internal static readonly string? WebGpuPluginDisabledReason =
        "the WebGPU plugin aborts the process on its first run under ONNX Runtime 1.30 (onnxruntime issue 28329)";

    /// <summary>The first WebGPU plugin refusal that is not about one model — a missing library, a failed
    /// registration, no GPU device — so later sessions skip straight to the CPU with the same reason.</summary>
    private static volatile string? _webGpuPluginRefusal;

    /// <summary>Test seam: clears the cached WebGPU-plugin refusal (D8) so one test's cache does not leak into another.</summary>
    internal static void ResetWebGpuPluginRefusalForTests() => _webGpuPluginRefusal = null;

    private static readonly Lock RegistrationGate = new();

    /// <summary>Plugin libraries registered with the process-wide <see cref="OrtEnv" />, by registration name.</summary>
    private static readonly Dictionary<string, string> RegisteredLibraries = new(StringComparer.Ordinal);

    /// <summary>The WebGPU plugin library the build copies into <c>webgpu/</c> on Windows and Linux; null when absent.</summary>
    internal static string? ResolveWebGpuPluginLibrary(string baseDirectory)
    {
        var fileName = OperatingSystem.IsWindows() ? "onnxruntime_providers_webgpu.dll" : "libonnxruntime_providers_webgpu.so";
        var candidate = Path.Combine(baseDirectory, WebGpuPluginDirectoryName, fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    ///     Registers a plugin library once per process under <paramref name="name" />; the same name and path
    ///     again is a no-op, a different path is refused. A registration that throws is not recorded.
    /// </summary>
    internal static string? EnsureRegistered(string name, string libraryPath, Action<string, string> register)
    {
        lock (RegistrationGate)
        {
            if (RegisteredLibraries.TryGetValue(name, out var registered))
            {
                return registered == libraryPath
                    ? null
                    : $"a different {name} provider library is already registered in this process (restart the server)";
            }

            register(name, libraryPath);
            RegisteredLibraries[name] = libraryPath;
            return null;
        }
    }

    private static string? EnsureRegistered(OrtEnv env, string name, string libraryPath) =>
        EnsureRegistered(name, libraryPath, env.RegisterExecutionProviderLibrary);

    /// <summary>
    ///     The WebGPU-plugin refusal cache (D8): reuses an earlier every-session refusal — a missing
    ///     library, a failed registration, no GPU device — without calling <paramref name="resolveAndRegister" />
    ///     again; a refusal from actually building one model's session on already-registered devices is
    ///     never passed here, so it is never cached, and the next session retries the plugin from scratch.
    /// </summary>
    internal static (IReadOnlyList<OrtEpDevice>? Devices, string? Refusal) WebGpuPluginDevicesOrCachedRefusal(
        Func<(IReadOnlyList<OrtEpDevice>? Devices, string? Refusal)> resolveAndRegister)
    {
        if (_webGpuPluginRefusal is { } cached)
        {
            return (null, cached);
        }

        var (devices, refusal) = resolveAndRegister();
        if (devices is null)
        {
            _webGpuPluginRefusal = refusal;
        }

        return (devices, refusal);
    }

    /// <summary>A session on the WebGPU plugin, or null with the refusal in <see cref="ExecutionProvider" />.
    /// Takes <see cref="GpuGate" /> like the built-in provider: both share one GPU context per process.</summary>
    private InferenceSession? CreatePluginWebGpuSessionOrNull(string modelPath, int intraOpThreads)
    {
        if (WebGpuPluginDisabledReason is { } disabled)
        {
            ExecutionProvider = $"{CpuProvider} (GPU refused: {disabled})";
            return null;
        }

        var (devices, refusal) = WebGpuPluginDevicesOrCachedRefusal(() =>
        {
            var library = ResolveWebGpuPluginLibrary(AppContext.BaseDirectory);
            if (library is null)
            {
                return (null, $"WebGPU plugin library not found under {Path.Combine(AppContext.BaseDirectory, WebGpuPluginDirectoryName)}");
            }

            lock (GpuGate)
            {
                return RegisterPluginGpuDevice(WebGpuExecutionProviderName, library,
                    "no WebGPU GPU device after registration (Linux needs libvulkan.so.1)");
            }
        });

        InferenceSession? session = null;
        if (devices is not null)
        {
            lock (GpuGate)
            {
                (session, refusal) = CreatePluginSessionOrNull(modelPath, intraOpThreads, devices);
            }
        }

        if (session is null)
        {
            ExecutionProvider = $"{CpuProvider} (GPU refused: {refusal})";
            return null;
        }

        ExecutionProvider = WebGpuProvider;
        return session;
    }

    /// <summary>Registers a plugin EP library and returns its first GPU device (never an integrated CPU
    /// fallback device the plugin may also expose), or null with the reason.</summary>
    private static (IReadOnlyList<OrtEpDevice>? Devices, string? Refusal) RegisterPluginGpuDevice(
        string epName, string libraryPath, string noDeviceReason)
    {
        try
        {
            var env = OrtEnv.Instance();
            var refusal = EnsureRegistered(env, epName, libraryPath);
            if (refusal is not null)
            {
                return (null, refusal);
            }

            var device = env.GetEpDevices()
                .FirstOrDefault(d => d.EpName == epName && d.HardwareDevice.Type == OrtHardwareDeviceType.GPU);
            return device is null ? (null, noDeviceReason) : ([device], null);
        }
        catch (Exception ex) when (IsPluginLoadFailure(ex))
        {
            return (null, ex.Message);
        }
    }

    /// <summary>A session on already-registered plugin devices, or null with ORT's reason.</summary>
    private static (InferenceSession? Session, string? Refusal) CreatePluginSessionOrNull(
        string modelPath, int intraOpThreads, IReadOnlyList<OrtEpDevice> devices)
    {
        try
        {
            using var options = new SessionOptions();
            if (intraOpThreads > 0)
            {
                options.IntraOpNumThreads = intraOpThreads;
            }

            foreach (var (key, value) in GpuSessionConfigEntries)
            {
                options.AddSessionConfigEntry(key, value);
            }

            options.AppendExecutionProvider(OrtEnv.Instance(), devices, new Dictionary<string, string>());
            return (new InferenceSession(modelPath, options), null);
        }
        catch (Exception ex) when (IsPluginLoadFailure(ex))
        {
            return (null, ex.Message);
        }
    }

    private const string CudaExecutionProviderName = "CUDAExecutionProvider";

    /// <summary>
    ///     A session on the user-supplied CUDA plugin library, or null with the reason. Construction takes
    ///     <see cref="GpuGate" /> like the WebGPU plugin's; runs do not, as CUDA sessions share no device context.
    /// </summary>
    private static InferenceSession? CreateCudaSessionOrNull(string modelPath, int intraOpThreads, string libraryPath,
        out string? refusalReason)
    {
        if (libraryPath.Length == 0)
        {
            refusalReason = "no provider library configured; run 'ai-raccoon settings model device cuda <path>'";
            return null;
        }

        if (!Path.IsPathFullyQualified(libraryPath))
        {
            refusalReason = $"provider library path must be absolute: {libraryPath}";
            return null;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            refusalReason = "requires Windows or Linux";
            return null;
        }

        if (!File.Exists(libraryPath))
        {
            refusalReason = $"provider library not found: {libraryPath}";
            return null;
        }

        lock (GpuGate)
        {
            var (devices, deviceRefusal) = RegisterPluginGpuDevice(CudaExecutionProviderName, libraryPath,
                "no CUDA GPU device after registration");
            if (devices is null)
            {
                refusalReason = deviceRefusal;
                return null;
            }

            (var session, refusalReason) = CreatePluginSessionOrNull(modelPath, intraOpThreads, devices);
            return session;
        }
    }

    /// <summary>What a plugin attempt may throw that must become a refusal rather than a failed construction.</summary>
    private static bool IsPluginLoadFailure(Exception ex) =>
        ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private const string MlxExecutionProviderName = "MLXExecutionProvider";
    private const string MlxPluginDirectoryName = "mlx";
    private const string MlxGraphFileName = "model_fp16_mlx.onnx";
    private const string MlxPluginLibraryFileName = "libonnxruntime_mlx_ep.dylib";

    private static readonly string[] MlxPluginFiles =
        [MlxPluginLibraryFileName, "libmlx.dylib", "libmlxc.dylib", "mlx.metallib"];

    /// <summary>The onnxruntime MLX plugin EP (ADR-0110) is proven only on macOS/Apple Silicon.</summary>
    private static bool MlxPlatformSupported() =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary>The plugin dylib ships beside the MLX runtime libraries it loads via @loader_path
    /// (docs/work/2026-09-24-onnx-runtime-providers-gpu-mlx.md F7); null unless all four are present.</summary>
    internal static string? ResolveMlxPluginDirectory(string baseDirectory)
    {
        var directory = Path.Combine(baseDirectory, MlxPluginDirectoryName);
        return MlxPluginFiles.All(file => File.Exists(Path.Combine(directory, file))) ? directory : null;
    }

    /// <summary>The rewritten graph (ADR-0110) that runs attention on MLX; null unless it sits beside <paramref name="modelPath" />.</summary>
    internal static string? ResolveMlxGraphPath(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var candidate = Path.Combine(directory, MlxGraphFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>A session on the MLX plugin EP running the rewritten graph, or null (with
    /// <paramref name="refusalReason" /> set) when the platform, the plugin files, the rewritten
    /// graph, or ORT itself refuses — the caller falls back to the existing WebGPU-then-CPU path.
    /// Construction runs on the dedicated <see cref="_mlxExecutor" /> thread, because the plugin is
    /// thread-affine: everything it does for this session must happen on the one thread it starts
    /// on. The executor is disposed here on any refusal; on success it is kept in
    /// <see cref="_mlxExecutor" /> for every later Run and for Dispose.</summary>
    private InferenceSession? CreateMlxSessionOrNull(string modelPath, int intraOpThreads, out string? refusalReason)
    {
        if (!MlxPlatformSupported())
        {
            refusalReason = "requires macOS on Apple Silicon";
            return null;
        }

        var pluginDirectory = ResolveMlxPluginDirectory(AppContext.BaseDirectory);
        if (pluginDirectory is null)
        {
            refusalReason = $"plugin files not found under {Path.Combine(AppContext.BaseDirectory, MlxPluginDirectoryName)}";
            return null;
        }

        var mlxModelPath = ResolveMlxGraphPath(modelPath);
        if (mlxModelPath is null)
        {
            refusalReason = $"no rewritten MLX graph ({MlxGraphFileName}) beside the model";
            return null;
        }

        var executor = new SingleThreadExecutor("airaccoon-mlx-session");
        var (session, error) = executor.Run(() => CreateMlxSessionOnCurrentThread(mlxModelPath, pluginDirectory, intraOpThreads));
        if (session is null)
        {
            executor.Dispose();
            refusalReason = error;
            return null;
        }

        _mlxExecutor = executor;
        _bucketRows = true;
        MlxCacheLimitApplied = executor.Run(() => new MlxCacheLimit(_logger).TryApply(pluginDirectory, MlxCacheLimit.DefaultBytes));
        refusalReason = null;
        return session;
    }

    private static (InferenceSession? Session, string? Error) CreateMlxSessionOnCurrentThread(
        string mlxModelPath, string pluginDirectory, int intraOpThreads)
    {
        try
        {
            var env = OrtEnv.Instance();
            var refusal = EnsureRegistered(env, MlxExecutionProviderName, Path.Combine(pluginDirectory, MlxPluginLibraryFileName));
            if (refusal is not null)
            {
                return (null, refusal);
            }

            var devices = env.GetEpDevices().Where(d => d.EpName == MlxExecutionProviderName).ToList();
            if (devices.Count == 0)
            {
                return (null, "no MLX device exposed by the plugin after registration");
            }

            using var options = new SessionOptions();
            if (intraOpThreads > 0)
            {
                options.IntraOpNumThreads = intraOpThreads;
            }

            foreach (var (key, value) in GpuSessionConfigEntries)
            {
                options.AddSessionConfigEntry(key, value);
            }

            options.AppendExecutionProvider(env, devices, new Dictionary<string, string>());
            return (new InferenceSession(mlxModelPath, options), null);
        }
        catch (OnnxRuntimeException ex)
        {
            return (null, ex.Message);
        }
        catch (DllNotFoundException ex)
        {
            return (null, ex.Message);
        }
    }

    object? IEmbeddingGenerator.GetService(Type serviceType, object? serviceKey) => null;

    // One row per session run: ORT keeps each run's activation peak, and a 32 x 510 batch on a 400M
    // model holds 3-7 GB for no per-row speedup (docs/work/2026-09-23-server-memory-usage.md F5/F6).
    private GeneratedEmbeddings<Embedding<float>> RunEachRow(
        IReadOnlyList<EncodedText> items, GeneratedEmbeddings<Embedding<float>> embeddings,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            RunBatch([item], embeddings, cancellationToken);
        }

        return embeddings;
    }

    private GeneratedEmbeddings<Embedding<float>> RunBatch(
        IReadOnlyList<EncodedText> items, GeneratedEmbeddings<Embedding<float>> embeddings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maxLen = Math.Min(_window, items.Max(i => i.Ids.Length));
        if (_bucketRows)
        {
            maxLen = LengthBuckets.PaddedLength(maxLen, _window);
        }

        LastSequenceLength = maxLen;
        var batch = items.Count;

        var inputIds = new long[batch * maxLen];
        var attentionMask = new long[batch * maxLen];
        for (var i = 0; i < batch; i++)
        {
            var ids = items[i].Ids;
            var mask = items[i].Mask;
            for (var s = 0; s < maxLen; s++)
            {
                if (s < ids.Length)
                {
                    inputIds[i * maxLen + s] = ids[s];
                    attentionMask[i * maxLen + s] = mask[s];
                }
            }
        }

        var feed = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [batch, maxLen])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [batch, maxLen]))
        };
        if (_inputNames.Contains("token_type_ids", StringComparer.Ordinal))
        {
            feed.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(new long[batch * maxLen], [batch, maxLen])));
        }

        if (_inputNames.Contains("position_ids", StringComparer.Ordinal))
        {
            var positions = new long[batch * maxLen];
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] = i % maxLen;
            }

            feed.Add(NamedOnnxValue.CreateFromTensor("position_ids", new DenseTensor<long>(positions, [batch, maxLen])));
        }

        feed.AddRange(EmptyKeyValueCache(batch));

        using var results = Run(feed);
        var output = results.First(r => r.Name == _outputName).AsTensor<float>();
        var dense = output as DenseTensor<float>
                    ?? throw new InvalidOperationException($"ONNX {_outputName} is not a dense tensor.");

        Pool(dense.Buffer.Span, dense.Dimensions, batch, maxLen, Dimension, attentionMask, _pooling, _normalization,
            _outputName, embeddings);
        return embeddings;
    }

    /// <summary>
    ///     Turns one session run's selected output into one vector per batch row. The output's own
    ///     RANK decides how, not the manifest's <c>pooling.mode</c> (issue #466): a rank-3
    ///     <c>[batch, sequence, dimensions]</c> tensor is token-level and gets that mode applied
    ///     here, while a rank-2 <c>[batch, dimensions]</c> tensor is already the embedding — a graph
    ///     that pools inside itself, which no mode can be applied to. A manifest whose mode was
    ///     inferred rather than read (<c>ModelDownloadPlanner</c>'s placeholder branch) is a guess;
    ///     the rank is a fact.
    /// </summary>
    internal static void Pool(ReadOnlySpan<float> output, ReadOnlySpan<int> dimensions, int batch, int maxLen,
        int dimension, ReadOnlySpan<long> attentionMask, string pooling, string normalization, string outputName,
        GeneratedEmbeddings<Embedding<float>> embeddings)
    {
        if (pooling == "model-output" && (dimensions.Length != 2 || dimensions[1] != dimension))
        {
            throw new InvalidOperationException(
                $"ONNX {outputName} must be a dense [batch, {dimension}] tensor for model-output pooling.");
        }

        if (dimensions.Length == 2)
        {
            PoolAlreadyPooledOutput(output, dimensions, batch, dimension, normalization, outputName, embeddings);
            return;
        }

        if (dimensions.Length != 3)
        {
            throw new InvalidOperationException(
                $"ONNX {outputName} is a rank-{dimensions.Length} tensor; a token-embeddings output must be "
                + $"[batch, sequence, {dimension}] and an already-pooled one [batch, {dimension}].");
        }

        var maskRow = new int[maxLen];
        for (var i = 0; i < batch; i++)
        {
            for (var s = 0; s < maxLen; s++)
            {
                maskRow[s] = (int)attentionMask[i * maxLen + s];
            }

            var row = output.Slice(i * maxLen * dimension, maxLen * dimension);
            var vector = pooling switch
            {
                "last-token" => normalization == "l2"
                    ? EmbeddingMath.L2Normalize(EmbeddingMath.LastTokenPool(row, maskRow, maxLen, dimension))
                    : EmbeddingMath.LastTokenPool(row, maskRow, maxLen, dimension),
                "cls" => normalization == "l2"
                    ? EmbeddingMath.ClsPoolAndNormalize(row, dimension)
                    : EmbeddingMath.ClsPool(row, dimension),
                // "mean" — the bundled path; mean+l2 takes the exact pre-WP3 code shape (G3).
                _ => normalization == "l2"
                    ? EmbeddingMath.MeanPoolAndNormalize(row, maskRow, maxLen, dimension)
                    : EmbeddingMath.MeanPool(row, maskRow, maxLen, dimension)
            };
            embeddings.Add(new Embedding<float>(vector));
        }
    }

    private static void PoolAlreadyPooledOutput(ReadOnlySpan<float> output, ReadOnlySpan<int> dimensions, int batch,
        int dimension, string normalization, string outputName, GeneratedEmbeddings<Embedding<float>> embeddings)
    {
        if (dimensions[1] != dimension || output.Length < batch * dimension)
        {
            throw new InvalidOperationException(
                $"ONNX {outputName} is a [batch, {dimensions[1]}] tensor of {output.Length} values; "
                + $"{batch} × {dimension} were expected.");
        }

        for (var i = 0; i < batch; i++)
        {
            var vector = output.Slice(i * dimension, dimension).ToArray();
            embeddings.Add(new Embedding<float>(normalization == "l2" ? EmbeddingMath.L2Normalize(vector) : vector));
        }
    }

    private static void ValidateInputNames(EngineDescriptor descriptor)
    {
        foreach (var name in descriptor.InputNames)
        {
            if (name is not ("input_ids" or "attention_mask" or "token_type_ids" or "position_ids") && !IsKeyValueCacheInput(name))
            {
                throw new InvalidOperationException(
                    $"Manifest model '{descriptor.Model}' declares unsupported ONNX input '{name}'; " +
                    "supported inputs: input_ids, attention_mask, token_type_ids, position_ids, past_key_values.*.");
            }
        }
    }

    private static bool IsKeyValueCacheInput(string name) => name.StartsWith(KeyValueCachePrefix, StringComparison.Ordinal);

    /// <summary>A decoder graph exported with a KV cache takes one past key and value per layer; a
    /// single full-sequence run feeds each an empty [batch, heads, 0, headDim] tensor.</summary>
    private IEnumerable<NamedOnnxValue> EmptyKeyValueCache(int batch)
    {
        foreach (var name in _inputNames.Where(IsKeyValueCacheInput))
        {
            var metadata = _session.InputMetadata[name];
            var shape = metadata.Dimensions.Select((d, axis) => axis == 0 ? batch : d < 0 ? 0 : d).ToArray();
            yield return metadata.ElementType == typeof(Float16)
                ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<Float16>(shape))
                : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(shape));
        }
    }

    private static string OutputNameFor(EngineDescriptor descriptor) => descriptor.Pooling == "model-output" ? descriptor.EmbeddingOutput! : descriptor.TokenEmbeddingsOutput;

    private static int ReadOutputDimension(InferenceSession session, string outputName, string modelName)
    {
        if (!session.OutputMetadata.TryGetValue(outputName, out var metadata))
        {
            throw new InvalidOperationException(
                $"ONNX model '{modelName}' has no output named '{outputName}' (check the manifest's onnx output names).");
        }

        var dims = metadata.Dimensions;
        if (dims is null || dims.Length == 0 || dims[^1] <= 0)
        {
            throw new InvalidOperationException(
                $"ONNX model '{modelName}' output '{outputName}' has no static final dimension " +
                $"(dims: [{string.Join(", ", dims ?? [])}]); the embedding dimension must be a compile-time constant.");
        }

        return (int)dims[^1];
    }

    /// <summary>
    ///     A run of ~100+ characters with no space or punctuation (this tokenizer's pretokenizer does
    ///     not split on newline/tab/CR) exceeds the per-word decomposition limit and collapses to a
    ///     single [UNK] — reporting a *tiny* token count for real content, invisible to any budget
    ///     ceiling check (docs/adr/0036). Newline-joined hash/id lists are the realistic trigger.
    /// </summary>
    private const int UnkCollapseMinChars = 100;

    private EncodedText Encode(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, true);
        if (ids.Count > _window)
        {
            Log.ChunkTruncatedAtEmbedTime(_logger, ids.Count, _window);
            ids = [.. ids.Take(_window)];
        }
        else if (ids.Count <= 3 && text.Length > UnkCollapseMinChars)
        {
            Log.ChunkPossiblyCollapsedToUnknownToken(_logger, text.Length, ids.Count);
        }

        var mask = new int[ids.Count];
        Array.Fill(mask, 1);
        return new EncodedText([.. ids], mask);
    }

    private readonly record struct EncodedText(int[] Ids, int[] Mask);

    public static partial class Log
    {
        /// <summary>
        ///     A STORED entry exceeded the window (docs/adr/0036). Should stay at zero once chunk budgets
        ///     are engine-aware — which it could not, while queries reached this same event (ADR-0071).
        /// </summary>
        [LoggerMessage(EventId = 414, Level = LogLevel.Warning,
            Message = "A stored entry was shortened before embedding: {ActualTokens} tokens exceeded the "
                      + "{MaxTokens}-token window, so the tail of that entry is missing from its search vector. "
                      + "The entry's text is intact; only what search matches on is short. Queries are trimmed "
                      + "separately and reported as event 416 — this one is always a write or an ingest.")]
        public static partial void ChunkTruncatedAtEmbedTime(ILogger logger, int actualTokens, int maxTokens);

        /// <summary>Fires when a long chunk tokenizes to almost nothing — likely an unknown-token collapse from a
        /// long punctuation-free, newline-joined run (docs/adr/0036) — and is embedded as noise. Wording is
        /// family-neutral (WP3 engineer S8): the collapse mechanism differs per tokenizer family.</summary>
        [LoggerMessage(EventId = 415, Level = LogLevel.Warning,
            Message = "Chunk possibly collapsed to an unknown token at embed time: {Chars} characters tokenized to only {ActualTokens} tokens")]
        public static partial void ChunkPossiblyCollapsedToUnknownToken(ILogger logger, int chars, int actualTokens);

        /// <summary>Issue #466: the graph emits a [batch, dimensions] vector, so the manifest's token-level
        /// pooling mode cannot apply and the graph's own pooling is used instead.</summary>
        [LoggerMessage(EventId = 417, Level = LogLevel.Warning,
            Message = "Model '{Model}' pools inside its own ONNX graph: output '{Output}' is [batch, dimensions], "
                      + "so the manifest's pooling mode '{Pooling}' cannot be applied and the graph's own vector is "
                      + "used as-is. Embedding is correct; the manifest's pooling.mode is wrong and should say "
                      + "'model-output'.")]
        public static partial void GraphPoolsItsOwnOutput(ILogger logger, string model, string pooling, string output);
    }
}
