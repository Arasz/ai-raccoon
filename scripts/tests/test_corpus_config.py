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

from corpus_config import EXCLUDE_GLOBS, INCLUDE_GLOBS, PROJECT_ID
from sources import enumerate_files

REPO_ROOT = Path(__file__).resolve().parents[2]

# Measured 2026-08-22 on the tree that produced docs-memory.db: 199 files, 1 608 970 bytes.
# The bands are ±35% around that, wide enough to absorb ordinary doc growth and narrow
# enough that a glob which stopped matching, or one that started swallowing docs/work,
# fails here instead of quietly hollowing out every rank gate downstream.
MEASURED_FILES = 199
MEASURED_BYTES = 1_608_970

EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")

# Placeholder addresses that are documentation, not contact details.
ALLOWED_EMAIL_DOMAINS = ("example.com", "domain.com", "nuget.org", "example.org")


def selection() -> list[str]:
    return [
        rel
        for _, rel, _ in enumerate_files(
            REPO_ROOT,
            include_globs=INCLUDE_GLOBS,
            exclude_globs=EXCLUDE_GLOBS,
            include_skill_references=False,
        )
    ]


class TestSelection:
    def test_project_id_is_this_repo(self):
        assert PROJECT_ID == "ai-raccoon"

    def test_file_count_stays_in_band(self):
        count = len(selection())
        assert 0.65 * MEASURED_FILES <= count <= 1.35 * MEASURED_FILES, (
            f"{count} files selected; measured {MEASURED_FILES} when docs-memory.db was built. "
            "A collapse means a glob stopped matching; a jump means an excluded tree got in."
        )

    def test_byte_total_stays_in_band(self):
        total = sum((REPO_ROOT / f).stat().st_size for f in selection())
        assert 0.65 * MEASURED_BYTES <= total <= 1.35 * MEASURED_BYTES, (
            f"{total} bytes selected; measured {MEASURED_BYTES} when docs-memory.db was built."
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
