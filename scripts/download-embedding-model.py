#!/usr/bin/env python3
"""Downloads and verifies the local embedding model used by AiRaccoon (port of download-embedding-model.sh)."""

import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from bundle import (  # noqa: E402
    BUNDLED_DIR,
    BUNDLED_FILES,
    BUNDLED_MANIFEST,
    BUNDLED_MLX_GRAPH,
    GGUF_NAME,
    GGUF_SHA256,
    GGUF_URL,
    VOCAB_NAME,
    VOCAB_SHA256,
    VOCAB_URL,
)
from download import Sha256MismatchError, fetch_verified, sha256_file  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parent.parent


def main(argv):
    model = argv[0] if argv else "onnx"
    out_dir = argv[1] if len(argv) > 1 else None
    if model == "onnx":
        models_dir = Path(out_dir) if out_dir else REPO_ROOT / "src" / "AiRaccoon" / "Models"
        fetch_verified(VOCAB_NAME, VOCAB_URL, VOCAB_SHA256, models_dir)
        target_dir = models_dir / BUNDLED_DIR
        entries = list(BUNDLED_FILES)
        manifest_name, manifest_sha = BUNDLED_MANIFEST
        manifest = target_dir / manifest_name
        if not manifest.is_file() or sha256_file(manifest) != manifest_sha:
            print("FAIL: %s is missing or does not match its pin (it is committed, never fetched)" % manifest, file=sys.stderr)
            return 1
        mlx_graph_name, mlx_graph_sha = BUNDLED_MLX_GRAPH
        mlx_graph = target_dir / mlx_graph_name
        if not mlx_graph.is_file() or sha256_file(mlx_graph) != mlx_graph_sha:
            print("FAIL: %s is missing or does not match its pin (it is committed, never fetched)" % mlx_graph, file=sys.stderr)
            return 1
    elif model == "gguf":
        default_root = os.environ.get("AIRACCOON_DATA_ROOT") or str(Path.home() / ".ai-raccoon")
        target_dir = Path(out_dir) if out_dir else Path(default_root) / "models"
        entries = [(GGUF_NAME, GGUF_URL, GGUF_SHA256)]
    else:
        print("unknown model: %s (choose onnx or gguf)" % model, file=sys.stderr)
        return 2
    for name, url, sha in entries:
        fetch_verified(name, url, sha, target_dir)
    if model == "onnx":
        print("")
        print("bundled model ready — it ships inside the tool package (packed from src/AiRaccoon/Models/%s)." % BUNDLED_DIR)
        print("custom path override: 'ai-raccoon model embedding set local /path/to/model.onnx' (the embedding.model settings row)")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Sha256MismatchError:
        sys.exit(1)
