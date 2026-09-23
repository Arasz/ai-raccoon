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
BUNDLED_MANIFEST = ("ai-raccoon.manifest.json", "7988517a61d24360da161b9fcc725c6f1a8ddbc356ec95ea30dccc6a53c0f143")

# Legacy single-file path (`model embedding set local <file>.onnx`) still tokenizes with this vocab.
VOCAB_NAME = "vocab.txt"
VOCAB_URL = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt"
VOCAB_SHA256 = "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"

GGUF_NAME = "all-MiniLM-L6-v2.Q5_K_M.gguf"
GGUF_URL = "https://huggingface.co/leliuga/all-MiniLM-L6-v2-GGUF/resolve/main/all-MiniLM-L6-v2.Q5_K_M.gguf"
GGUF_SHA256 = "908c82ac3849f9ca23158117cec614bd8ec404040d8794c35b4c81242bf315e3"
