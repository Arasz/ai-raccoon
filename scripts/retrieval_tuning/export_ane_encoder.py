#!/usr/bin/env python3
"""Export granite-embedding-small-english-r2 as a CoreML-EP-friendly model dir.

Writes model_fp16.onnx + model_fp16.onnx_data (standard ONNX ops only; the name is the one the
harness Engine in chunk_window.py opens, whatever --dtype is), copies tokenizer.json,
tokenizer_config.json and config.json from the bundled model dir, and writes an
ai-raccoon.manifest.json cloned from the bundled one with every listed sha256 recomputed.
--dtype fp16 (default) keeps weights and compute in half with float32 outputs, like the product's
model_fp16.onnx; inputs stay int64 either way.

Usage:
    python3 export_ane_encoder.py --layout plain|ane --out <dir> [--max-len 1024] [--dtype fp16|fp32]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

import onnx  # noqa: E402
import torch  # noqa: E402

from retrieval_tuning import ane_encoder  # noqa: E402

COPIED_FILES = ("tokenizer.json", "tokenizer_config.json", "config.json")
MODEL_NAME = "model_fp16.onnx"
OPSET = 17
DYNAMIC_AXES = {
    "input_ids": {0: "batch_size", 1: "sequence_length"},
    "attention_mask": {0: "batch_size", 1: "total_sequence_length"},
    "last_hidden_state": {0: "batch_size", 1: "sequence_length"},
    "sentence_embedding": {0: "batch_size"},
}


def build_module(layout: str, max_len: int, dtype: str) -> torch.nn.Module:
    hf_model = ane_encoder.load_hf_model()
    if layout == "plain":
        encoder = ane_encoder.PlainEncoder(hf_model, max_len).eval()
    elif layout == "ane":
        encoder = ane_encoder.AneEncoder(hf_model, max_len).eval()
    else:
        raise ValueError(f"unknown layout {layout!r}")
    if dtype == "fp32":
        return encoder
    if dtype == "fp16":
        return ane_encoder.Fp32Outputs(encoder).eval()
    raise ValueError(f"unknown dtype {dtype!r}")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def _export_onnx(module: torch.nn.Module, out: Path) -> None:
    ids = torch.full((2, 16), ane_encoder.PAD_ID, dtype=torch.int64)
    mask = torch.ones((2, 16), dtype=torch.int64)
    with tempfile.TemporaryDirectory() as tmp:
        raw = Path(tmp) / "raw.onnx"
        with torch.no_grad():
            torch.onnx.export(module, (ids, mask), str(raw), dynamo=False, opset_version=OPSET,
                              input_names=["input_ids", "attention_mask"],
                              output_names=["last_hidden_state", "sentence_embedding"],
                              dynamic_axes=DYNAMIC_AXES, do_constant_folding=True)
        model = onnx.load(str(raw))
    onnx.save_model(model, str(out / MODEL_NAME), save_as_external_data=True, all_tensors_to_one_file=True,
                    location=f"{MODEL_NAME}_data", size_threshold=1024)


def _manifest(out: Path, layout: str, max_len: int, dtype: str) -> dict:
    manifest = json.loads((ane_encoder.BUNDLED_DIR / "ai-raccoon.manifest.json").read_text())
    manifest["contextWindowTokens"] = min(manifest["contextWindowTokens"], max_len)
    manifest["onnx"]["files"] = [{"path": name, "sha256": _sha256(out / name)}
                                 for name in (MODEL_NAME, f"{MODEL_NAME}_data")]
    for entry in manifest["tokenizer"]["files"] + manifest["provenanceFiles"]:
        entry["sha256"] = _sha256(out / entry["path"])
    manifest["export"] = {"layout": layout, "maxLen": max_len, "weights": ane_encoder.HF_REPO,
                          "dtype": dtype, "opset": OPSET}
    return manifest


def export_model_dir(layout: str, out: Path, max_len: int = 1024, dtype: str = "fp16") -> Path:
    """Export one layout into out (created if missing) and return it."""
    out = Path(out).resolve()
    models_root = ane_encoder.BUNDLED_DIR.parent.resolve()
    if out == models_root or models_root in out.parents:
        raise ValueError(f"refusing to write under the product models root {models_root}")
    out.mkdir(parents=True, exist_ok=True)

    _export_onnx(build_module(layout, max_len, dtype), out)
    for name in COPIED_FILES:
        shutil.copyfile(ane_encoder.BUNDLED_DIR / name, out / name)
    manifest = _manifest(out, layout, max_len, dtype)
    (out / "ai-raccoon.manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return out


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--layout", choices=["plain", "ane"], required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--max-len", type=int, default=1024)
    parser.add_argument("--dtype", choices=["fp16", "fp32"], default="fp16")
    args = parser.parse_args()
    print(export_model_dir(args.layout, args.out, args.max_len, args.dtype))


if __name__ == "__main__":
    main()
