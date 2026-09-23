"""Tests for retrieval_tuning.code_corpus.validate_manifest (E1 §validator).

RED-first: the fixture below builds one deliberately-valid corpus on disk (a
manifest dict + matching files/, LICENSES/ tree under tmp_path acting as a
fake repo root). Every mutation test starts from that valid baseline and
breaks exactly one thing, so each assertion targets one validator check: a
tampered byte must fail the sha256 check, a 2-file band must fail the
per-language/per-band count, and so on.
"""

from __future__ import annotations

import hashlib
from pathlib import Path

import pytest

from retrieval_tuning.code_corpus import (
    ALLOWED_LICENSES,
    CODE_CORPUS_DIR,
    SIZE_BANDS,
    validate_manifest,
)

BAND_LINES = {"small": 25, "medium": 200, "large": 700}
MIT_TEXT = "Permission is hereby granted, free of charge, to any person obtaining a copy ...\n"


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _write(path: Path, nonblank_lines: int) -> bytes:
    path.parent.mkdir(parents=True, exist_ok=True)
    text = "".join(f"line {i}\n" for i in range(nonblank_lines))
    data = text.encode("utf-8")
    path.write_bytes(data)
    return data


def _external_family_rows(root: Path, family: str, language: str) -> list[dict]:
    """9 vendored src rows (3 per band) for one external family/language."""
    rows = []
    for band in ("small", "medium", "large"):
        for i in range(3):
            rel = f"{language}/{band}-{i}.txt"
            vendored = f"{CODE_CORPUS_DIR}/files/{family}/{rel}"
            data = _write(root / vendored, BAND_LINES[band])
            rows.append({
                "family": family,
                "repo": f"example/{family}",
                "sha": "a" * 40,
                "path": rel,
                "vendoredPath": vendored,
                "language": language,
                "sizeBand": band,
                "role": "src",
                "lines": BAND_LINES[band],
                "sha256": _sha256(data),
                "license": "MIT",
            })
    (root / CODE_CORPUS_DIR / "LICENSES" / family).parent.mkdir(parents=True, exist_ok=True)
    (root / CODE_CORPUS_DIR / "LICENSES" / family).write_text(MIT_TEXT)
    return rows


def build_valid_corpus(tmp_path: Path) -> tuple[dict, Path]:
    """3 external families (Widget/Gadget/Gizmo), one src file in Gizmo's
    9 swapped for a `self` row (snapshotted under files/self/ like any family),
    plus one external and one self src/test pair. All 3 families held out.
    """
    root = tmp_path
    rows: list[dict] = []

    rows += _external_family_rows(root, "widget-repo", "Widget")
    rows += _external_family_rows(root, "gadget-repo", "Gadget")

    gizmo_rows = _external_family_rows(root, "gizmo-repo", "Gizmo")
    # Swap one external "large" Gizmo row for a self row (same language/band
    # count, different provenance) so both resolution paths are exercised.
    swapped = gizmo_rows.pop()
    assert swapped["sizeBand"] == "large"
    self_rel = "src/Widgets/BigGizmo.cs"
    self_vendored = f"{CODE_CORPUS_DIR}/files/self/{self_rel}"
    self_data = _write(root / self_vendored, BAND_LINES["large"])
    gizmo_rows.append({
        "family": "self",
        "repo": "self",
        "sha": None,
        "path": self_rel,
        "vendoredPath": self_vendored,
        "language": "Gizmo",
        "sizeBand": "large",
        "role": "src",
        "lines": BAND_LINES["large"],
        "sha256": _sha256(self_data),
        "license": "MIT",
    })
    rows += gizmo_rows

    # An external src/test pair (Widget's first small file).
    widget_small = rows[0]
    assert widget_small["family"] == "widget-repo" and widget_small["sizeBand"] == "small"
    test_vendored = f"{CODE_CORPUS_DIR}/files/widget-repo/Widget/small-0.test.txt"
    test_data = _write(root / test_vendored, 10)
    rows.append({
        "family": "widget-repo",
        "repo": "example/widget-repo",
        "sha": "a" * 40,
        "path": "Widget/small-0.test.txt",
        "vendoredPath": test_vendored,
        "language": "Widget",
        "sizeBand": None,
        "role": "test",
        "lines": 10,
        "sha256": _sha256(test_data),
        "license": "MIT",
        "pairOf": widget_small["path"],
    })

    # A self src/test pair (the swapped-in Gizmo self file).
    self_test_rel = "tests/Widgets/BigGizmoTests.cs"
    self_test_vendored = f"{CODE_CORPUS_DIR}/files/self/{self_test_rel}"
    self_test_data = _write(root / self_test_vendored, 10)
    rows.append({
        "family": "self",
        "repo": "self",
        "sha": None,
        "path": self_test_rel,
        "vendoredPath": self_test_vendored,
        "language": "Gizmo",
        "sizeBand": None,
        "role": "test",
        "lines": 10,
        "sha256": _sha256(self_test_data),
        "license": "MIT",
        "pairOf": self_rel,
    })

    manifest = {
        "languages": ["Widget", "Gadget", "Gizmo"],
        "heldOutFamilies": ["widget-repo", "gadget-repo", "gizmo-repo"],
        "files": rows,
    }
    return manifest, root


class TestBaselineIsValid:
    def test_valid_corpus_has_no_problems(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        assert validate_manifest(manifest, root) == []


class TestPerLanguageBandCounts:
    def test_a_two_file_band_fails(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        # Drop one of Widget's 3 "small" src rows -> band now has 2.
        manifest["files"] = [
            row for row in manifest["files"]
            if not (row["family"] == "widget-repo" and row["sizeBand"] == "small"
                    and row["path"] == "Widget/small-1.txt")
        ]
        problems = validate_manifest(manifest, root)
        assert any("small" in p and "Widget" in p for p in problems)

    def test_missing_language_fails(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        manifest["files"] = [row for row in manifest["files"] if row["language"] != "Gadget"]
        problems = validate_manifest(manifest, root)
        assert any("Gadget" in p for p in problems)


class TestBandMatchesMeasuredLines:
    def test_band_disagreeing_with_actual_content_fails(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        target = next(
            row for row in manifest["files"]
            if row["family"] == "gadget-repo" and row["sizeBand"] == "medium"
        )
        # Rewrite the vendored bytes to "large"-band length without updating
        # sizeBand/lines/sha256 -> measured lines disagree with the declared band.
        new_data = _write(root / target["vendoredPath"], BAND_LINES["large"])
        target["sha256"] = _sha256(new_data)  # keep sha honest; only the band is wrong now
        problems = validate_manifest(manifest, root)
        assert any(target["path"] in p for p in problems)


class TestSha256MatchesBytes:
    def test_a_tampered_byte_fails_the_sha_check(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        target = next(row for row in manifest["files"] if row["family"] == "widget-repo" and row["role"] == "src")
        path = root / target["vendoredPath"]
        data = bytearray(path.read_bytes())
        data[0] ^= 0xFF  # flip a bit -> bytes no longer match the recorded sha256
        path.write_bytes(bytes(data))
        problems = validate_manifest(manifest, root)
        assert any("sha256" in p and target["path"] in p for p in problems)

    def test_self_file_sha_is_also_checked(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        target = next(row for row in manifest["files"] if row["family"] == "self" and row["role"] == "src")
        path = root / target["vendoredPath"]
        data = bytearray(path.read_bytes())
        data[0] ^= 0xFF
        path.write_bytes(bytes(data))
        problems = validate_manifest(manifest, root)
        assert any("sha256" in p for p in problems)


class TestLicensesFile:
    def test_missing_licenses_file_for_an_external_family_fails(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        (root / CODE_CORPUS_DIR / "LICENSES" / "gadget-repo").unlink()
        problems = validate_manifest(manifest, root)
        assert any("gadget-repo" in p and "LICENSES" in p for p in problems)

    def test_self_family_never_needs_a_licenses_file(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        assert not (root / CODE_CORPUS_DIR / "LICENSES" / "self").exists()
        assert validate_manifest(manifest, root) == []


class TestLicenseEnum:
    def test_disallowed_license_fails(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        for row in manifest["files"]:
            if row["family"] == "widget-repo":
                row["license"] = "GPL-3.0"
        problems = validate_manifest(manifest, root)
        assert any("license" in p.lower() and "GPL-3.0" in p for p in problems)

    def test_every_allowed_license_is_accepted(self, tmp_path):
        for license_ in sorted(ALLOWED_LICENSES):
            manifest, root = build_valid_corpus(tmp_path / license_)
            for row in manifest["files"]:
                if row["family"] == "widget-repo":
                    row["license"] = license_
            assert validate_manifest(manifest, root) == []


class TestHeldOutFamilyCount:
    def test_fewer_than_three_held_out_families_fails(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        manifest["heldOutFamilies"] = ["widget-repo", "gadget-repo"]
        problems = validate_manifest(manifest, root)
        assert any("held" in p.lower() for p in problems)

    def test_exactly_three_held_out_families_passes(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        assert len(manifest["heldOutFamilies"]) == 3
        assert validate_manifest(manifest, root) == []


def test_size_bands_and_licenses_come_from_the_seed():
    from retrieval_tuning import repo_data

    seed = repo_data.CORPORA["code-eval-corpus"]
    assert SIZE_BANDS == {band: tuple(bounds) for band, bounds in seed["sizeBands"].items()}
    assert ALLOWED_LICENSES == set(seed["allowedLicenses"])


class TestSelfHeldOut:
    def test_a_pinned_path_that_is_not_a_self_row_fails(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        manifest["selfHeldOut"] = ["src/Widgets/Gone.cs"]
        problems = validate_manifest(manifest, root)
        assert any("selfHeldOut" in p and "src/Widgets/Gone.cs" in p for p in problems), problems

    def test_a_pinned_self_row_passes(self, tmp_path):
        manifest, root = build_valid_corpus(tmp_path)
        manifest["selfHeldOut"] = ["src/Widgets/BigGizmo.cs"]
        assert validate_manifest(manifest, root) == []

    def test_the_committed_manifest_pins_the_seed_held_out_paths(self):
        import json

        from retrieval_tuning import repo_data

        repo = Path(__file__).resolve().parents[2]
        manifest = json.loads((repo / CODE_CORPUS_DIR / "MANIFEST.json").read_text())
        assert manifest.get("selfHeldOut") == repo_data.CORPORA["code-eval-corpus"]["selfHeldOut"]


def test_a_row_without_a_snapshot_under_files_is_rejected(tmp_path):
    """A live repo path drifts the moment someone edits it; every row must be a frozen copy."""
    manifest, root = build_valid_corpus(tmp_path)
    row = next(r for r in manifest["files"] if r["family"] == "self")
    live = root / row["path"]
    live.parent.mkdir(parents=True, exist_ok=True)
    live.write_bytes((root / row["vendoredPath"]).read_bytes())
    row["vendoredPath"] = None

    problems = validate_manifest(manifest, root)

    assert any("vendoredPath" in p and row["path"] in p for p in problems), problems
