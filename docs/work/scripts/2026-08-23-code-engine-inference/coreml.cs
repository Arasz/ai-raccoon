#:property ManagePackageVersionsCentrally=false
#:package Microsoft.ML.OnnxRuntime@1.29.0

// Produces §3.1 of docs/work/2026-08-23-code-engine-inference-research.md:
// CoreML-EP partition counts and CPU-vs-CoreML output parity for one ONNX model.
//
//   dotnet run coreml.cs <model.onnx> [mlprogram|neuralnetwork]
//
// Pass the backend to reproduce either row of §3.1's table. Run with the ORT
// verbose log visible to harvest the GetCapability / node-placement lines and,
// under mlprogram, Apple's E5RT shape rejections (§3.2).

using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Figures are quoted in the research record, so they must not pick up a decimal comma.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var modelPath = args[0];
var backend = args.Length > 1 ? args[1] : "mlprogram";
var flags = backend switch
{
    "mlprogram" => CoreMLFlags.COREML_FLAG_CREATE_MLPROGRAM,
    "neuralnetwork" => CoreMLFlags.COREML_FLAG_USE_NONE,
    _ => throw new ArgumentException($"backend must be mlprogram or neuralnetwork, got '{backend}'"),
};

Console.WriteLine($"ORT managed assembly: {typeof(SessionOptions).Assembly.GetName().Version}, backend: {backend}");

float[] Run(bool coreml, int seq)
{
    var so = new SessionOptions { IntraOpNumThreads = 5 };
    if (coreml)
    {
        so.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE;
        so.AppendExecutionProvider_CoreML(flags);
    }

    using var session = new InferenceSession(modelPath, so);

    // Synthetic ids over the real vocab range (22,739), bracketed by <s>=2 and </s>=3.
    // Deterministic, so CPU and CoreML see byte-identical input.
    var ids = new long[seq];
    var mask = new long[seq];
    for (var i = 0; i < seq; i++)
    {
        ids[i] = i == 0 ? 2 : (i == seq - 1 ? 3 : 100 + (i * 37 % 20000));
        mask[i] = 1;
    }

    var inputs = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, seq])),
        NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, seq])),
    };

    using var results = session.Run(inputs);
    var first = results.First();
    var t = first.AsTensor<float>();
    Console.WriteLine($"[{(coreml ? "coreml" : "cpu")}] output '{first.Name}' dims=[{string.Join(",", t.Dimensions.ToArray())}] seq={seq}");
    return t.ToArray();
}

static double Cos(float[] a, float[] b)
{
    double dot = 0, na = 0, nb = 0;
    for (var i = 0; i < a.Length; i++)
    {
        dot += a[i] * b[i];
        na += a[i] * a[i];
        nb += b[i] * b[i];
    }

    return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
}

foreach (var seq in new[] { 64, 128 })
{
    var cpu = Run(false, seq);
    var cml = Run(true, seq);
    var maxAbs = 0.0;
    for (var i = 0; i < cpu.Length; i++)
    {
        maxAbs = Math.Max(maxAbs, Math.Abs(cpu[i] - cml[i]));
    }
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"PARITY seq={seq} len={cpu.Length} cosine={Cos(cpu, cml):F10} maxAbsDelta={maxAbs:E3}"));
}
