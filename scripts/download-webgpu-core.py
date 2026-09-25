#!/usr/bin/env python3
"""Downloads and verifies the WebGPU-enabled ONNX Runtime core for win-x64, win-arm64 and linux-x64
(ADR-0115): a pinned, sha256-verified fetch of the onnxruntime-node tarball, unpacked per RID into
the git-ignored webgpu-core/ folder. Directory.Build.targets swaps each core in place of the NuGet
core at build time. Never committed."""

import shutil
import sys
import tarfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from bundle import (  # noqa: E402
    WEBGPU_CORE_FILES,
    WEBGPU_CORE_TARBALL_NAME,
    WEBGPU_CORE_TARBALL_SHA256,
    WEBGPU_CORE_TARBALL_URL,
)
from download import Sha256MismatchError, fetch_verified  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_TARGET = REPO_ROOT / "src" / "AiRaccoon" / "webgpu-core"


def extract_core_files(tarball: Path, target_dir: Path) -> None:
    """Unpacks each RID's core (and, on Windows, Dawn's shader compiler) into target_dir/<rid>/."""
    with tarfile.open(tarball, "r:gz") as archive:
        for rid, (directory, files) in WEBGPU_CORE_FILES.items():
            rid_dir = target_dir / rid
            rid_dir.mkdir(parents=True, exist_ok=True)
            for member, name in files:
                source = archive.extractfile("%s/%s" % (directory, member))
                destination = rid_dir / name
                partial = destination.with_name(destination.name + ".partial")
                with source, open(partial, "wb") as sink:
                    shutil.copyfileobj(source, sink)
                partial.replace(destination)
                print("extracted: %s" % destination)


def main(argv: list[str]) -> int:
    target_dir = Path(argv[0]) if argv else DEFAULT_TARGET
    target_dir.mkdir(parents=True, exist_ok=True)
    tarball = fetch_verified(WEBGPU_CORE_TARBALL_NAME, WEBGPU_CORE_TARBALL_URL, WEBGPU_CORE_TARBALL_SHA256,
                             target_dir / ".tarball-cache")
    extract_core_files(tarball, target_dir)
    print("")
    print("WebGPU core ready at %s (win-x64, win-arm64, linux-x64 packs, ADR-0115)." % target_dir)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Sha256MismatchError:
        sys.exit(1)
