#!/usr/bin/env python3
"""Verifies the packed global tool ships the bundled ONNX model + vocab, byte-verified against the pinned SHA-256 (FR-NM-3; see docs/work/features-native-memory/native-memory.feature). Port of verify-tool-package.sh."""

import shutil
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from package_verify import detect_rid, pack_and_verify  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parent.parent
CSPROJ = REPO_ROOT / "src" / "AiRaccoon" / "AiRaccoon.csproj"


def main(argv):
    rid = detect_rid()
    tmp_dir = tempfile.mkdtemp(prefix="ai-raccoon-verify-pkg.")
    try:
        return pack_and_verify(CSPROJ, rid, tmp_dir)
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
