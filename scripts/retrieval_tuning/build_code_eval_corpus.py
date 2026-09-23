#!/usr/bin/env python3
"""E1 — deterministic builder for the multi-language code eval corpus.

Reads seeds from ``data/corpora/code-eval-corpus.json`` (repo_data.CORPORA)
and, for every non-``self`` file entry, fetches the file's bytes at its
pinned commit SHA from ``raw.githubusercontent.com`` (stdlib ``urllib``, no
git clone). ``self`` entries are read straight from this repository instead
of being copied. For every external family it also fetches that family's
LICENSE file once. It writes:

- ``scripts/retrieval_tuning/code-corpus/files/<family>/<orig-path>`` — the
  vendored bytes for every non-self file.
- ``scripts/retrieval_tuning/code-corpus/LICENSES/<family>`` — that family's
  LICENSE text, fetched at the same pinned SHA.
- ``scripts/retrieval_tuning/code-corpus/MANIFEST.json`` — one row per file:
  ``{family, repo, sha, path, vendoredPath, language, sizeBand, role, lines,
  sha256, license, pairOf?}``, plus the top-level ``languages`` and
  ``heldOutFamilies`` lists ``retrieval_tuning.code_corpus.validate_manifest``
  checks against.

``--check`` verifies the corpus already on disk against the seed and the
validator, with no network calls at all.

Usage:
    python scripts/retrieval_tuning/build_code_eval_corpus.py
    python scripts/retrieval_tuning/build_code_eval_corpus.py --check

Exit code 0 = corpus built/verified clean; 1 = any file failed to fetch, or
(``--check``) the corpus disagrees with the seed or fails validation.
Import-safe: no side effects at import time (all logic is in functions).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts" / "src"))
from retrieval_tuning import code_corpus, repo_data  # noqa: E402

RAW_BASE = "https://raw.githubusercontent.com"
SELF_FAMILY = "self"
CODE_CORPUS_ROOT = REPO_ROOT / code_corpus.CODE_CORPUS_DIR
FILES_DIR = CODE_CORPUS_ROOT / "files"
LICENSES_DIR = CODE_CORPUS_ROOT / "LICENSES"
MANIFEST_PATH = CODE_CORPUS_ROOT / "MANIFEST.json"

USER_AGENT = "ai-raccoon-code-eval-corpus-builder"


class FetchError(RuntimeError):
    """A pinned raw.githubusercontent.com fetch failed."""


def _load_seed() -> dict:
    return repo_data.CORPORA["code-eval-corpus"]


def _repos_by_family(seed: dict) -> dict[str, dict]:
    return {repo["family"]: repo for repo in seed["repos"]}


def _fetch_bytes(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.read()
    except (urllib.error.URLError, TimeoutError) as exc:
        raise FetchError(f"{url}: {exc}") from exc


def _nonblank_lines(data: bytes) -> int:
    text = data.decode("utf-8", errors="replace")
    return sum(1 for line in text.splitlines() if line.strip())


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _raw_url(repo: str, sha: str, path: str) -> str:
    return f"{RAW_BASE}/{repo}/{sha}/{path}"


def build(seed: dict) -> dict:
    """Fetch every file + license and write files/, LICENSES/, MANIFEST.json.

    Network-only for non-self entries. Returns the manifest dict written.
    """
    repos = _repos_by_family(seed)
    rows: list[dict] = []
    fetched_licenses: set[str] = set()

    for entry in seed["files"]:
        family = entry["family"]
        repo_meta = repos[family]
        language = entry["language"]
        role = entry["role"]
        size_band = entry.get("sizeBand")
        path = entry["path"]

        vendored_rel = f"{code_corpus.CODE_CORPUS_DIR}/files/{family}/{path}"
        vendored_abs = REPO_ROOT / vendored_rel
        if family == SELF_FAMILY:
            # Snapshot, not a live reference: an edit to the repo file must not move the corpus.
            data = vendored_abs.read_bytes() if vendored_abs.exists() else (REPO_ROOT / path).read_bytes()
        else:
            repo = repo_meta["repo"]
            sha = repo_meta["sha"]
            data = _fetch_bytes(_raw_url(repo, sha, path))
        vendored_abs.parent.mkdir(parents=True, exist_ok=True)
        vendored_abs.write_bytes(data)
        vendored_path = vendored_rel
        if family != SELF_FAMILY:
            if family not in fetched_licenses:
                license_data = _fetch_bytes(_raw_url(repo, sha, repo_meta["licensePath"]))
                LICENSES_DIR.mkdir(parents=True, exist_ok=True)
                (LICENSES_DIR / family).write_bytes(license_data)
                fetched_licenses.add(family)

        row = {
            "family": family,
            "repo": repo_meta["repo"],
            "sha": repo_meta["sha"],
            "path": path,
            "vendoredPath": vendored_path,
            "language": language,
            "sizeBand": size_band,
            "role": role,
            "lines": _nonblank_lines(data),
            "sha256": _sha256(data),
            "license": repo_meta["license"],
        }
        if "pairOf" in entry:
            row["pairOf"] = entry["pairOf"]
        rows.append(row)

    manifest = {
        "languages": seed["languages"],
        "heldOutFamilies": seed["heldOutFamilies"],
        "selfHeldOut": seed["selfHeldOut"],
        "files": rows,
    }
    CODE_CORPUS_ROOT.mkdir(parents=True, exist_ok=True)
    MANIFEST_PATH.write_text(json.dumps(manifest, indent=2, sort_keys=False) + "\n")
    return manifest


def _manifest_key(row: dict) -> tuple[str, str, str]:
    return (row["family"], row["path"], row["role"])


def check(seed: dict) -> list[str]:
    """Verify the on-disk corpus against the seed and the validator. No network."""
    problems: list[str] = []

    if not MANIFEST_PATH.exists():
        return [f"{MANIFEST_PATH}: missing — run the builder without --check first"]

    manifest = json.loads(MANIFEST_PATH.read_text())

    seed_keys = {(e["family"], e["path"], e["role"]) for e in seed["files"]}
    manifest_keys = {_manifest_key(row) for row in manifest.get("files", [])}
    missing = seed_keys - manifest_keys
    extra = manifest_keys - seed_keys
    for family, path, role in sorted(missing):
        problems.append(f"{family}:{path} ({role}): in the seed but not in MANIFEST.json — rebuild")
    for family, path, role in sorted(extra):
        problems.append(f"{family}:{path} ({role}): in MANIFEST.json but not in the seed — rebuild")

    if manifest.get("languages") != seed.get("languages"):
        problems.append("MANIFEST.json 'languages' disagrees with the seed — rebuild")
    if manifest.get("heldOutFamilies") != seed.get("heldOutFamilies"):
        problems.append("MANIFEST.json 'heldOutFamilies' disagrees with the seed — rebuild")
    if manifest.get("selfHeldOut") != seed.get("selfHeldOut"):
        problems.append("MANIFEST.json 'selfHeldOut' disagrees with the seed — rebuild")

    problems.extend(code_corpus.validate_manifest(manifest, REPO_ROOT))
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check", action="store_true",
        help="verify the corpus already on disk against the seed and validator (no network)",
    )
    args = parser.parse_args(argv)

    seed = _load_seed()

    if args.check:
        problems = check(seed)
        if problems:
            print(f"FAIL: {len(problems)} problem(s):", file=sys.stderr)
            for problem in problems:
                print(f"  - {problem}", file=sys.stderr)
            return 1
        print(f"OK: {len(seed['files'])} files verified against the seed and validator.")
        return 0

    try:
        manifest = build(seed)
    except FetchError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1

    problems = code_corpus.validate_manifest(manifest, REPO_ROOT)
    if problems:
        print(f"FAIL: built corpus failed validation ({len(problems)} problem(s)):", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    print(f"OK: built {len(manifest['files'])} files -> {MANIFEST_PATH}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
