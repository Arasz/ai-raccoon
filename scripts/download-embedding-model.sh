#!/usr/bin/env bash
# Downloads and verifies the small local embedding model used by AiRaccoon's
# local embedding engine (sqlite-memory's llama.cpp integration).
#
# Default: all-MiniLM-L6-v2 Q5_K_M (~21 MB, Apache-2.0) — the smallest model we
# verified against the memory extension. Alternative: nomic-embed-text-v1.5
# Q8_0 (~139 MB, Apache-2.0), the model sqlite-memory's README documents.
#
# Usage:
#   scripts/download-embedding-model.sh [--model all-minilm|nomic] [output-dir]
#
# The output file is meant to be pointed at via memory_configure(provider=local)
# or AIRACCOON_TEST_GGUF for the embedding integration/E2E tests.
set -euo pipefail

MODEL="${1:-all-minilm}"
OUT_DIR="${2:-${AIRACCOON_DATA_ROOT:-$HOME/.ai-raccoon}/models}"
mkdir -p "$OUT_DIR"

case "$MODEL" in
  all-minilm)
    NAME="all-MiniLM-L6-v2.Q5_K_M.gguf"
    URL="https://huggingface.co/leliuga/all-MiniLM-L6-v2-GGUF/resolve/main/${NAME}"
    SHA256="908c82ac3849f9ca23158117cec614bd8ec404040d8794c35b4c81242bf315e3"
    ;;
  nomic)
    NAME="nomic-embed-text-v1.5.Q8_0.gguf"
    URL="https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF/resolve/main/${NAME}"
    SHA256="" # not pinned yet — verify once and fill in
    ;;
  *)
    echo "unknown model: $MODEL (choose all-minilm or nomic)" >&2
    exit 2
    ;;
esac

TARGET="$OUT_DIR/$NAME"
if [ -f "$TARGET" ] && [ -n "$SHA256" ] && [ "$(shasum -a 256 "$TARGET" | cut -d' ' -f1)" = "$SHA256" ]; then
  echo "already downloaded and verified: $TARGET"
  exit 0
fi

echo "downloading $NAME -> $TARGET"
curl -L --fail --progress-bar -o "$TARGET.part" "$URL"
mv "$TARGET.part" "$TARGET"

if [ -n "$SHA256" ]; then
  ACTUAL="$(shasum -a 256 "$TARGET" | cut -d' ' -f1)"
  if [ "$ACTUAL" != "$SHA256" ]; then
    echo "SHA-256 mismatch: expected $SHA256, got $ACTUAL" >&2
    rm -f "$TARGET"
    exit 1
  fi
  echo "verified: $TARGET"
else
  echo "WARNING: $MODEL sha256 not pinned; computed $(shasum -a 256 "$TARGET" | cut -d' ' -f1)" >&2
fi

echo ""
echo "use it with:"
echo "  memory_configure(projectId=..., provider=\"local\", model=\"$TARGET\")"
echo "  or export AIRACCOON_TEST_GGUF=$TARGET"
