#!/usr/bin/env python3
"""Regenerates tests/AiRaccoon.Tests/Retrieval/assets/reference-topk.json from the pinned
reference extension (sqlite-memory 1.3.5 + sqlite-vector 1.0.0 + the pinned GGUF model).

Run this when the corpus changes (scripts/generate-benchmark-corpus.py) or when the pinned
extension/model version moves — then commit the updated golden file. The committed file is
the parity oracle for P6's fused-retriever gate, so regeneration is a deliberate act.
"""

import os
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
FILTER = "FullyQualifiedName~Retrieval.Reference.GoldenFileTests.GoldenFile_MatchesFreshReferenceRun"


def main() -> int:
    env = dict(os.environ)
    env["AIRACCOON_HARNESS_REGENERATE_GOLDEN"] = "1"
    result = subprocess.run(
        ["dotnet", "test", "tests/AiRaccoon.Tests", "--filter", FILTER],
        cwd=str(REPO_ROOT),
        env=env,
    )
    if result.returncode != 0:
        return result.returncode
    print("")
    print("golden file regenerated: tests/AiRaccoon.Tests/Retrieval/assets/reference-topk.json")
    print("review and commit the diff.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
