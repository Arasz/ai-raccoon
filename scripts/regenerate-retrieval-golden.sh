#!/usr/bin/env bash
# Regenerates tests/AiRaccoon.Tests/Retrieval/assets/reference-topk.json from the pinned
# reference extension (sqlite-memory 1.3.5 + sqlite-vector 1.0.0 + the pinned GGUF model).
#
# Run this when the corpus changes (scripts/generate-benchmark-corpus.py) or when the pinned
# extension/model version moves — then commit the updated golden file. The committed file is
# the parity oracle for P6's fused-retriever gate, so regeneration is a deliberate act.
set -euo pipefail
cd "$(dirname "$0")/.."

AIRACCOON_HARNESS_REGENERATE_GOLDEN=1 dotnet test tests/AiRaccoon.Tests \
  --filter "FullyQualifiedName~Retrieval.Reference.GoldenFileTests.GoldenFile_MatchesFreshReferenceRun"

echo ""
echo "golden file regenerated: tests/AiRaccoon.Tests/Retrieval/assets/reference-topk.json"
echo "review and commit the diff."
