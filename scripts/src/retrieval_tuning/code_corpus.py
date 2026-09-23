"""Validator for the multi-language code eval corpus (plan §E1).

`validate_manifest` checks a loaded MANIFEST.json against the files it
describes: per-language/per-band file counts, declared band vs. measured
non-blank lines, sha256 integrity, a LICENSES file per external family, an
allowed license, and the held-out family count (ADR-0056 split).
"""

from __future__ import annotations

import hashlib
from collections import Counter
from pathlib import Path

CODE_CORPUS_DIR = "scripts/retrieval_tuning/code-corpus"

SIZE_BANDS: dict[str, tuple[int, int]] = {
    "small": (20, 80),
    "medium": (150, 400),
    "large": (600, 1500),
}

FILES_PER_BAND = 3
BANDS_PER_LANGUAGE = len(SIZE_BANDS)
FILES_PER_LANGUAGE = FILES_PER_BAND * BANDS_PER_LANGUAGE

ALLOWED_LICENSES = {"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause"}

MIN_HELD_OUT_FAMILIES = 3

SELF_FAMILY = "self"


def _resolve_file_path(row: dict, root: Path) -> Path:
    """A row's bytes: its frozen copy under `files/`."""
    return root / row["vendoredPath"]


def _check_snapshots(files: list[dict], problems: list[str]) -> bool:
    """Every row, self included, must be a frozen copy under files/; a live path drifts on edit."""
    prefix = f"{CODE_CORPUS_DIR}/files/"
    ok = True
    for row in files:
        vendored = row.get("vendoredPath") or ""
        if not vendored.startswith(prefix):
            problems.append(f"{row['path']}: vendoredPath must be a snapshot under {prefix} (got {vendored!r})")
            ok = False
    return ok


def _count_nonblank_lines(path: Path) -> int:
    text = path.read_text(encoding="utf-8", errors="replace")
    return sum(1 for line in text.splitlines() if line.strip())


def _sha256_of(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _check_band_counts(manifest: dict, files: list[dict], problems: list[str]) -> None:
    """Every declared (or observed) language needs exactly 3 src files per band."""
    src_rows = [row for row in files if row.get("role", "src") == "src"]
    by_language: dict[str, Counter] = {}
    for row in src_rows:
        by_language.setdefault(row["language"], Counter())[row.get("sizeBand")] += 1

    expected_languages = set(manifest.get("languages", [])) | set(by_language)
    for language in sorted(expected_languages):
        counts = by_language.get(language, Counter())
        total = sum(counts.values())
        if total != FILES_PER_LANGUAGE:
            problems.append(
                f"{language}: expected {FILES_PER_LANGUAGE} src files, found {total}"
            )
        for band in SIZE_BANDS:
            found = counts.get(band, 0)
            if found != FILES_PER_BAND:
                problems.append(
                    f"{language}/{band}: expected {FILES_PER_BAND} src files, found {found}"
                )
        unexpected = set(counts) - set(SIZE_BANDS) - {None}
        for band in sorted(unexpected):
            problems.append(f"{language}: unknown sizeBand '{band}'")


def _check_band_matches_measured_lines(files: list[dict], root: Path, problems: list[str]) -> None:
    for row in files:
        if row.get("role", "src") != "src":
            continue
        band = row.get("sizeBand")
        path = _resolve_file_path(row, root)
        if not path.exists():
            problems.append(f"{row['path']}: vendored file is missing ({path})")
            continue
        if band not in SIZE_BANDS:
            continue
        lo, hi = SIZE_BANDS[band]
        try:
            measured = _count_nonblank_lines(path)
        except OSError as exc:
            problems.append(f"{row['path']}: could not read file to measure lines ({exc})")
            continue
        if not (lo <= measured <= hi):
            problems.append(
                f"{row['path']}: sizeBand '{band}' expects {lo}-{hi} non-blank lines, "
                f"measured {measured}"
            )
        recorded = row.get("lines")
        if recorded is not None and recorded != measured:
            problems.append(
                f"{row['path']}: manifest 'lines' ({recorded}) disagrees with measured "
                f"non-blank lines ({measured})"
            )


def _check_sha256(files: list[dict], root: Path, problems: list[str]) -> None:
    for row in files:
        path = _resolve_file_path(row, root)
        if not path.exists():
            continue  # already reported by the band/line check
        expected = row.get("sha256")
        if not expected:
            problems.append(f"{row['path']}: manifest row is missing sha256")
            continue
        actual = _sha256_of(path)
        if actual.lower() != str(expected).lower():
            problems.append(
                f"{row['path']}: sha256 mismatch (manifest {expected}, actual {actual})"
            )


def _check_licenses(files: list[dict], root: Path, problems: list[str]) -> None:
    external_families = sorted({
        row["family"] for row in files if row.get("repo") != SELF_FAMILY and row["family"] != SELF_FAMILY
    })
    licenses_dir = root / CODE_CORPUS_DIR / "LICENSES"
    for family in external_families:
        license_path = licenses_dir / family
        if not license_path.is_file():
            problems.append(f"{family}: missing LICENSES file ({license_path})")


def _check_license_enum(files: list[dict], problems: list[str]) -> None:
    seen: dict[str, str] = {}
    for row in files:
        license_ = row.get("license")
        if license_ not in ALLOWED_LICENSES:
            key = f"{row['family']}:{row['path']}"
            if key not in seen:
                problems.append(
                    f"{row['path']}: license '{license_}' is not one of {sorted(ALLOWED_LICENSES)}"
                )
                seen[key] = license_


def _check_held_out_families(manifest: dict, files: list[dict], problems: list[str]) -> None:
    declared = set(manifest.get("heldOutFamilies", []))
    external_families = {
        row["family"] for row in files if row.get("repo") != SELF_FAMILY and row["family"] != SELF_FAMILY
    }
    valid = declared & external_families
    if len(valid) < MIN_HELD_OUT_FAMILIES:
        problems.append(
            f"heldOutFamilies: expected at least {MIN_HELD_OUT_FAMILIES} external families, "
            f"found {len(valid)} ({sorted(valid)})"
        )


def validate_manifest(manifest: dict, root: Path) -> list[str]:
    """Structural + integrity validation of a loaded MANIFEST.json.

    `root` is the repository root: vendored files resolve at
    `root/<vendoredPath>` (self-family files too, snapshotted under files/self/), and
    per-family LICENSES files live under `root/<CODE_CORPUS_DIR>/LICENSES/`.
    Returns a list of problems (empty = valid).
    """
    root = Path(root)
    files = manifest.get("files", [])
    problems: list[str] = []

    if not files:
        problems.append("manifest has no files")
        return problems

    if not _check_snapshots(files, problems):
        return problems
    _check_band_counts(manifest, files, problems)
    _check_band_matches_measured_lines(files, root, problems)
    _check_sha256(files, root, problems)
    _check_licenses(files, root, problems)
    _check_license_enum(files, problems)
    _check_held_out_families(manifest, files, problems)

    return problems
