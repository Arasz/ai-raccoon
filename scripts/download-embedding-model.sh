#!/usr/bin/env bash
# Downloads and verifies the small local embedding model used by AiRaccoon's
# in-process ONNX embedding engine (FR-NM-3).
#
# Default (onnx): the int8-quantized all-MiniLM-L6-v2 ONNX model (~23 MB) plus its
# BERT vocab, pinned by SHA-256, into src/AiRaccoon/Models/ — the location packed
# into the tool package and resolved at runtime via AppContext.BaseDirectory/Models
# (env override: AIRACCOON_EMBEDDING_MODEL for a custom model path).
#
# Legacy (gguf): the Q5_K_M GGUF model used by the golden-retrieval harness reference
# embedder (tests/Retrieval/assets bootstrap handles that copy on its own).
#
# Usage:
#   scripts/download-embedding-model.sh [onnx|gguf] [output-dir]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODEL="${1:-onnx}"
ONNX_OUT_DIR="${2:-$REPO_ROOT/src/AiRaccoon/Models}"

case "$MODEL" in
  onnx)
    NAME="model_qint8_arm64.onnx"
    URL="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/${NAME}"
    SHA256="4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474"
    VOCAB_NAME="vocab.txt"
    VOCAB_URL="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/${VOCAB_NAME}"
    VOCAB_SHA256="07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"
    ;;
  gguf)
    NAME="all-MiniLM-L6-v2.Q5_K_M.gguf"
    URL="https://huggingface.co/leliuga/all-MiniLM-L6-v2-GGUF/resolve/main/${NAME}"
    SHA256="908c82ac3849f9ca23158117cec614bd8ec404040d8794c35b4c81242bf315e3"
    VOCAB_NAME=""
    ;;
  *)
    echo "unknown model: $MODEL (choose onnx or gguf)" >&2
    exit 2
    ;;
esac

fetch_verified() {
  local name="$1" url="$2" expected="$3" target="$4"
  if [ -f "$TARGET_DIR/$name" ] && [ "$(shasum -a 256 "$TARGET_DIR/$name" | cut -d' ' -f1)" = "$expected" ]; then
    echo "already downloaded and verified: $TARGET_DIR/$name"
    return 0
  fi
  echo "downloading $name -> $TARGET_DIR/$name"
  curl -L --fail --progress-bar -o "$TARGET_DIR/$name.part" "$url"
  mv "$TARGET_DIR/$name.part" "$TARGET_DIR/$name"
  local actual
  actual="$(shasum -a 256 "$TARGET_DIR/$name" | cut -d' ' -f1)"
  if [ "$actual" != "$expected" ]; then
    echo "SHA-256 mismatch for $name: expected $expected, got $actual" >&2
    rm -f "$TARGET_DIR/$name"
    exit 1
  fi
  echo "verified: $TARGET_DIR/$name"
}

TARGET_DIR="$ONNX_OUT_DIR"
if [ "$MODEL" = "gguf" ]; then
  TARGET_DIR="${2:-${AIRACCOON_DATA_ROOT:-$HOME/.ai-raccoon}/models}"
fi
mkdir -p "$TARGET_DIR"

fetch_verified "$NAME" "$URL" "$SHA256" "$TARGET_DIR"
if [ -n "$VOCAB_NAME" ]; then
  fetch_verified "$VOCAB_NAME" "$VOCAB_URL" "$VOCAB_SHA256" "$TARGET_DIR"
fi

if [ "$MODEL" = "onnx" ]; then
  echo ""
  echo "bundled model ready — it ships inside the tool package (packed from src/AiRaccoon/Models)."
  echo "custom path override: memory_configure(provider=\"local\", model=\"/path/to/model.onnx\")"
  echo "                      or export AIRACCOON_EMBEDDING_MODEL=/path/to/model.onnx"
fi
