using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Points ONNX Runtime's native import at the WebGPU-enabled core the Windows and Linux x64 packages
///     ship under <c>webgpu/</c> (ADR-0115); without that folder ORT keeps loading its NuGet core. Call
///     <see cref="EnsureWebGpuCore" /> from an entry-point module initializer, before any ORT API runs.
/// </summary>
public static class OnnxRuntimeCore
{
    private const string OrtLibraryName = "onnxruntime";
    private const string WebGpuCoreDirectoryName = "webgpu";

    private static readonly Lock Gate = new();
    private static bool _initialized;

    /// <summary>The core the resolver loads: <c>webgpu/</c>'s ORT library, or null when this package has none.</summary>
    public static string? LoadedWebGpuCore { get; private set; }

    /// <summary>Idempotent: registers the resolver when <c>webgpu/</c> holds a core.</summary>
    public static void EnsureWebGpuCore()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            var core = ResolveWebGpuCore(AppContext.BaseDirectory);
            if (core is null)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) =>
                name == OrtLibraryName ? NativeLibrary.Load(core) : IntPtr.Zero);
            LoadedWebGpuCore = core;
        }
    }

    /// <summary>The WebGPU core under <paramref name="baseDirectory" />/webgpu/ for this OS, or null when absent.</summary>
    internal static string? ResolveWebGpuCore(string baseDirectory)
    {
        var fileName = OperatingSystem.IsWindows() ? "onnxruntime.dll"
            : OperatingSystem.IsLinux() ? "libonnxruntime.so"
            : null;
        if (fileName is null)
        {
            return null;
        }

        var candidate = Path.Combine(baseDirectory, WebGpuCoreDirectoryName, fileName);
        return File.Exists(candidate) ? candidate : null;
    }
}
