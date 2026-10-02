"""Selection rules for the committed fixture corpus (ADR-0090).

Deliberately NOT a snapshot pin on the file count. The selection is evaluated against the
live tree, so a hard count would go red on every PR that adds an ADR — including the PR that
introduced these rules, which adds ADR-0090 itself. A pin nobody can keep green gets widened
without thought, which is worse than no pin. These assert the properties that actually keep
the retrieval gates honest: both document families survive, excluded trees stay out, the
selection cannot silently collapse or balloon, and no real address rides along.
"""

from pathlib import Path
import re
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from corpus_config import PROJECT_ID, select

REPO_ROOT = Path(__file__).resolve().parents[2]

# Measured 2026-09-30 on tree 73b0b0c0 by corpus_config.select: 262 files, 2 202 170 bytes —
# +63 files / +593 200 bytes (+31.7% / +36.9%) from the previous pin (199 files, 1 608 970
# bytes, 2026-08-22). The committed docs-memory.db is still the 2026-08-22 build. The ±35%
# bands are a tripwire for aggregate selection drift, not a tolerance that absorbs this
# repo's growth: the previous pin went red on the +36.9% byte growth in 39 days while its
# +31.7% file count stayed green, and halving or doubling the pin fails both tests in both
# directions. They do not catch every per-glob loss: dropping .ai-badger/skills/*/SKILL.md
# leaves 209 files / 1 655 355 bytes with all 9 tests green (13 of 14 single-glob losses stay
# inside the bands; only the docs/adr/*.md loss falls outside) — a stopped non-ADR glob can
# hide here until something else moves the aggregate past a bound.
MEASURED_FILES = 262
MEASURED_BYTES = 2_202_170

EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")

# Placeholder addresses that are documentation, not contact details.
ALLOWED_EMAIL_DOMAINS = ("example.com", "domain.com", "nuget.org", "example.org")


def selection() -> list[str]:
    return select(REPO_ROOT)


class TestSelection:
    def test_project_id_is_this_repo(self):
        assert PROJECT_ID == "ai-raccoon"

    def test_file_count_stays_in_band(self):
        count = len(selection())
        assert 0.65 * MEASURED_FILES <= count <= 1.35 * MEASURED_FILES, (
            f"{count} files selected; the 2026-09-30 live-tree pin is {MEASURED_FILES}. "
            "A collapse means a glob stopped matching; a jump means an excluded tree got in."
        )

    def test_byte_total_stays_in_band(self):
        total = sum((REPO_ROOT / f).stat().st_size for f in selection())
        assert 0.65 * MEASURED_BYTES <= total <= 1.35 * MEASURED_BYTES, (
            f"{total} bytes selected; the 2026-09-30 live-tree pin is {MEASURED_BYTES}."
        )

    def test_both_document_families_are_present(self):
        files = selection()
        docs = [f for f in files if f.startswith("docs/")]
        badger = [f for f in files if f.startswith(".ai-badger/")]
        # RetrievalTuningSetsTests asserts the corpus carries more than one generator; a
        # selection that collapsed to one family would hollow that gate out silently.
        assert len(docs) > 50, f"docs/ family too thin: {len(docs)}"
        assert len(badger) > 50, f".ai-badger/ family too thin: {len(badger)}"

    def test_adrs_are_the_backbone(self):
        adrs = [f for f in selection() if f.startswith("docs/adr/")]
        assert len(adrs) > 50, f"ADR family too thin: {len(adrs)}"

    def test_excluded_trees_stay_out(self):
        files = selection()
        for forbidden in ("docs/work/", "docs/plans/", "docs/reviews/",
                          ".ai-badger/skills/learned/", ".github/"):
            offenders = [f for f in files if f.startswith(forbidden)]
            assert not offenders, f"{forbidden} leaked into the corpus: {offenders[:5]}"

    def test_only_git_tracked_files_are_selected(self):
        import subprocess

        tracked = set(
            subprocess.run(
                ["git", "-C", str(REPO_ROOT), "ls-files"],
                capture_output=True, text=True, check=True, timeout=60,
            ).stdout.splitlines()
        )
        offenders = [f for f in selection() if f not in tracked]
        # Observed: a sibling branch's unmerged ADR, untracked in this worktree, put 29
        # chunks into a regenerated bank. The fixture must be reproducible from a clean clone.
        assert not offenders, f"untracked files would be baked into the fixture: {offenders}"

    def test_skill_reference_files_are_not_selected(self):
        offenders = [f for f in selection() if "/references/" in f]
        assert not offenders, f"skill reference files are excluded by measurement: {offenders[:5]}"

    def test_no_real_email_address_rides_along(self):
        offenders = {}
        for rel in selection():
            text = (REPO_ROOT / rel).read_text(encoding="utf-8", errors="replace")
            real = [
                address
                for address in set(EMAIL.findall(text))
                if not address.lower().endswith(ALLOWED_EMAIL_DOMAINS)
            ]
            if real:
                offenders[rel] = sorted(real)
        assert not offenders, (
            "ai-raccoon#414 removed a corpus carrying the owner's address in 94 rows. "
            f"Real addresses found in the replacement selection: {offenders}"
        )
