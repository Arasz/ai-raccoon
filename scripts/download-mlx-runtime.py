#!/usr/bin/env python3
"""Downloads and verifies the onnxruntime MLX plugin execution provider for AiRaccoon's opt-in
`settings model device mlx` (ADR-0110). Mirrors download-embedding-model.py: a pinned, sha256-
verified fetch into a git-ignored folder the osx-arm64 pack picks up at build time — the four
native files are never committed."""

import shutil
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))

from bundle import MLX_RUNTIME_FILES, MLX_WHEEL_NAME, MLX_WHEEL_SHA256, MLX_WHEEL_URL  # noqa: E402
from download import Sha256MismatchError, fetch_verified  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_TARGET = REPO_ROOT / "src" / "AiRaccoon" / "mlx-runtime"


def extract_runtime_files(wheel: Path, target_dir: Path) -> None:
    """Unpacks the plugin dylib and its MLX runtime libraries from the wheel's onnxruntime_ep_mlx/
    package directory — flat, so they stay siblings for the wheel's own @loader_path install names."""
    with zipfile.ZipFile(wheel) as archive:
        for name in MLX_RUNTIME_FILES:
            member = "onnxruntime_ep_mlx/%s" % name
            destination = target_dir / name
            with archive.open(member) as source, open(destination, "wb") as sink:
                shutil.copyfileobj(source, sink)
            print("extracted: %s" % destination)


def main(argv: list[str]) -> int:
    target_dir = Path(argv[0]) if argv else DEFAULT_TARGET
    target_dir.mkdir(parents=True, exist_ok=True)
    wheel = fetch_verified(MLX_WHEEL_NAME, MLX_WHEEL_URL, MLX_WHEEL_SHA256, target_dir / ".wheel-cache")
    extract_runtime_files(wheel, target_dir)
    print("")
    print("MLX runtime ready at %s (osx-arm64 pack only, ADR-0110)." % target_dir)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Sha256MismatchError:
        sys.exit(1)
