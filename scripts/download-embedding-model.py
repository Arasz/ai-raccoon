#!/usr/bin/env python3
"""Downloads and verifies the local embedding model used by AiRaccoon (port of download-embedding-model.sh)."""

import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from bundle import (  # noqa: E402
    CODE_TOKENIZER_NAME,
    CODE_TOKENIZER_SHA256,
    CODE_TOKENIZER_URL,
    GGUF_NAME,
    GGUF_SHA256,
    GGUF_URL,
    MODEL_NAME,
    MODEL_SHA256,
    MODEL_URL,
    VOCAB_NAME,
    VOCAB_SHA256,
    VOCAB_URL,
)
from download import Sha256MismatchError, fetch_verified  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parent.parent


def main(argv):
    model = argv[0] if argv else "onnx"
    out_dir = argv[1] if len(argv) > 1 else None
    if model == "onnx":
        target_dir = Path(out_dir) if out_dir else REPO_ROOT / "src" / "AiRaccoon" / "Models"
        entries = [
            (MODEL_NAME, MODEL_URL, MODEL_SHA256),
            (VOCAB_NAME, VOCAB_URL, VOCAB_SHA256),
            (CODE_TOKENIZER_NAME, CODE_TOKENIZER_URL, CODE_TOKENIZER_SHA256),
        ]
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
        print("bundled model ready — it ships inside the tool package (packed from src/AiRaccoon/Models).")
        print("custom path override: 'ai-raccoon model set local /path/to/model.onnx' (the embedding.model settings row)")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Sha256MismatchError:
        sys.exit(1)
