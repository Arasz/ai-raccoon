using System.Runtime.CompilerServices;
using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Tests;

/// <summary>Creates the ONNX Runtime environment with ai-raccoon's log callback before any test touches ORT, as production does.</summary>
internal static class TestOrtInit
{
    [ModuleInitializer]
    public static void Initialize() => _ = OrtSessionLog.Environment;
}
