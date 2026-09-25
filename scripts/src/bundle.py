"""Model bundle contract: filenames, URLs, and SHA-256 pins for AiRaccoon's local embedding models."""

# ADR-0108: the one bundled engine (memory and code) — granite-embedding-small-english-r2, fp16
# export, laid out as a manifest model directory under Models/. Pinned to the repo commit so a
# re-upload upstream cannot change what a build packs; the product's own `model download` wrote
# the manifest these pins match.
BUNDLED_DIR = "granite-embedding-small-english-r2"
_BUNDLED_REPO = "https://huggingface.co/onnx-community/granite-embedding-small-english-r2-ONNX/resolve/1dc7835ba0cb9c76a3618d0bf0c427c97671b3c8/"
BUNDLED_FILES = (
    ("model_fp16.onnx", _BUNDLED_REPO + "onnx/model_fp16.onnx", "ee200de55cb2f94e858aabca54be7697a9c0805a14c858ee26ad0922b05f57d7"),
    ("model_fp16.onnx_data", _BUNDLED_REPO + "onnx/model_fp16.onnx_data", "28d16e29cd623f25cc6fa0968700c5bc31036466091a5fa06d1353c1777f050e"),
    ("tokenizer.json", _BUNDLED_REPO + "tokenizer.json", "feeb83348dcb033bc6b9d2e1f7906ca9eb2d122845000c9416d894d7c2927149"),
    ("config.json", _BUNDLED_REPO + "config.json", "1a1710c20911da8c96179716bf44058e54cca6fa7952cce77be83ef05edae3ee"),
    ("tokenizer_config.json", _BUNDLED_REPO + "tokenizer_config.json", "ce06781b38bb393db68c9e0709bddd31ef5d88f2c6fbb3fd9f369778fb85e451"),
)
# Written by `ai-raccoon model download`, committed beside the model; verified, never fetched.
BUNDLED_MANIFEST = ("ai-raccoon.manifest.json", "777268daa83ea925bfbfe4e020e62d0232be6df48189280a117a3de65bbd4ff6")

# ADR-0110: the opt-in MLX execution provider's rewritten graph — attention as standard ops, Range
# folded to CumSum — generated from model_fp16.onnx by scripts/src/make_mlx_graph.py. It references
# the same, unchanged model_fp16.onnx_data. Committed beside the model, verified, never fetched,
# same convention as BUNDLED_MANIFEST.
BUNDLED_MLX_GRAPH = ("model_fp16_mlx.onnx", "6652d8c068d32511bf3d209fbe40af43c743a3b412fdb0dddee6535a0a5e7239")

# Legacy single-file path (`model embedding set local <file>.onnx`) still tokenizes with this vocab.
VOCAB_NAME = "vocab.txt"
VOCAB_URL = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt"
VOCAB_SHA256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"

GGUF_NAME = "all-MiniLM-L6-v2.Q5_K_M.gguf"
GGUF_URL = "https://huggingface.co/leliuga/all-MiniLM-L6-v2-GGUF/resolve/main/all-MiniLM-L6-v2.Q5_K_M.gguf"
GGUF_SHA256 = "908c82ac3849f9ca23158117cec614bd8ec404040d8794c35b4c81242bf315e3"

# ADR-0110: the opt-in MLX execution provider (`settings model device mlx`, osx-arm64 only). The
# wheel is a stock PyPI release, never rebuilt; its four native files (they load each other through
# @loader_path, so they must stay siblings) are unpacked into a git-ignored folder at build time and
# never committed — MIT-licensed binaries, ~43 MB compressed.
MLX_WHEEL_NAME = "onnxruntime_ep_mlx-0.29.6-py3-none-macosx_14_0_arm64.whl"
MLX_WHEEL_URL = (
    "https://files.pythonhosted.org/packages/ef/f2/6f0070bd74b6ea3cef52ecb8da2e821de044241a1f6fc6e4cb2195aa966e/"
    + MLX_WHEEL_NAME
)
MLX_WHEEL_SHA256 = "6ce838e9c39b799a122a6cf022c93dfb6c6f74540825acf49ece913621a7fda3"
MLX_RUNTIME_FILES = ("libonnxruntime_mlx_ep.dylib", "libmlx.dylib", "libmlxc.dylib", "mlx.metallib")

# ADR-0115: ONNX Runtime's own onnxruntime-node build of the core, which has WebGPU compiled in for
# Windows and glibc Linux x64 (the NuGet core does not). Same ORT release and commit as the managed
# package; only the native core and Dawn's DirectX shader compiler are taken, never the Node binding.
WEBGPU_CORE_TARBALL_NAME = "onnxruntime-node-1.30.0.tgz"
WEBGPU_CORE_TARBALL_URL = "https://registry.npmjs.org/onnxruntime-node/-/" + WEBGPU_CORE_TARBALL_NAME
WEBGPU_CORE_TARBALL_SHA256 = "6e3390d6b783e7be946fad629292799da28d0b42f84856e50d2c1b0383291e75"
# RID -> (directory inside the tarball, ((member, file name the tool loads), ...)).
WEBGPU_CORE_FILES = {
    "linux-x64": ("package/bin/napi-v6/linux/x64", (("libonnxruntime.so.1", "libonnxruntime.so"),)),
    "win-x64": ("package/bin/napi-v6/win32/x64", (("onnxruntime.dll", "onnxruntime.dll"),
                                                   ("dxcompiler.dll", "dxcompiler.dll"),
                                                   ("dxil.dll", "dxil.dll"))),
    "win-arm64": ("package/bin/napi-v6/win32/arm64", (("onnxruntime.dll", "onnxruntime.dll"),
                                                       ("dxcompiler.dll", "dxcompiler.dll"),
                                                       ("dxil.dll", "dxil.dll"))),
}
