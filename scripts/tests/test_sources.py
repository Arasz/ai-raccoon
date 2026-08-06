"""Pin CURRENT file-enumeration behavior (scripts/ingest-jsaa-docs.py) — byte-identical move."""

from pathlib import Path

from sources import _matches_exclude, classify_file, enumerate_files


class TestMatchesExclude:
    def test_exact_file_match(self):
        assert _matches_exclude(".ai-badger/state.json") is True

    def test_directory_prefix_exclusion(self):
        assert _matches_exclude(".ai-badger/task-tracking/t1.json") is True
        assert _matches_exclude("docs/work/2026-08-06-x.md") is True

    def test_remember_now_excluded(self):
        assert _matches_exclude(".remember/now.md") is True

    def test_remember_today_glob_is_exact_match_only_today(self):
        # Current behavior: ".remember/today-*.md" only matches itself (no fnmatch).
        assert _matches_exclude(".remember/today-2026-07-20.md") is False

    def test_other_excluded_paths(self):
        assert _matches_exclude(".github/workflows/publish.yml") is True
        assert _matches_exclude("node_modules/x/y.js") is True
        assert _matches_exclude(".git/config") is True
        assert _matches_exclude("docs/state.json") is True

    def test_include_paths_not_excluded(self):
        assert _matches_exclude("docs/adr/0011-x.md") is False
        assert _matches_exclude("CLAUDE.md") is False


class TestClassifyFile:
    def test_type_keys_and_contexts(self):
        cases = {
            "docs/adr/0011-x.md": ("adr", "docs:adr"),
            "docs/architecture.md": ("architecture", "docs:architecture"),
            "docs/data-model.md": ("architecture", "docs:architecture"),
            "docs/flows.md": ("architecture", "docs:architecture"),
            "docs/requirements.md": ("architecture", "docs:architecture"),
            "docs/CHANGELOG.md": ("architecture", "docs:architecture"),
            "docs/explanation/x.md": ("explanation", "docs:explanation"),
            "docs/how-to/x.md": ("howto", "docs:how-to"),
            "docs/reference/x.md": ("reference", "docs:reference"),
            "docs/rules/x.md": ("rules", "docs:rules"),
            "docs/tutorials/x.md": ("tutorials", "docs:tutorials"),
            "docs/legacy/x.md": ("legacy", "docs:legacy"),
            "docs/meta/trust-debt.md": ("meta", "docs:meta"),
            "docs/README.md": ("root-md", "docs:architecture"),
            "docs/other.md": ("architecture", "docs:architecture"),
            ".ai-badger/invariants/x.md": ("invariants", "ai-badger:invariants"),
            ".ai-badger/skills/foo/SKILL.md": ("skills", "ai-badger:skills"),
            ".ai-badger/agents/x.md": ("agents", "ai-badger:agents"),
            ".ai-badger/instructions/x.md": ("instructions", "ai-badger:instructions"),
            ".ai-badger/agent-instructions/x.md": ("agent-model", "ai-badger:instructions"),
            ".ai-badger/config.json": ("config", "ai-badger:instructions"),
            ".ai-badger/delegation.md": ("config", "ai-badger:instructions"),
            ".ai-badger/copilot-instructions.md": ("config", "ai-badger:instructions"),
            ".remember/recent.md": ("remember", "remember:operational"),
            "README.md": ("root-md", "docs:architecture"),
            "CLAUDE.md": ("root-md", "docs:architecture"),
            "HERMES.md": ("root-md", "docs:architecture"),
            "REVIEW.md": ("root-md", "docs:architecture"),
            "infra/README.md": ("infra", "docs:architecture"),
            "weird/thing.md": ("unknown", "docs:architecture"),
        }
        for rel, expected in cases.items():
            assert classify_file(rel) == expected, rel


FAKE_TREE_FILES = [
    "docs/adr/0001-x.md",
    "docs/architecture.md",
    "docs/explanation/why.md",
    "docs/how-to/h.md",
    "docs/reference/r.md",
    "docs/rules/ru.md",
    "docs/tutorials/t.md",
    "docs/legacy/l.md",
    "docs/meta/baseline.json",
    "docs/meta/trust-debt.md",
    "docs/README.md",
    "docs/work/skip-me.md",          # excluded dir
    "docs/brand/logo.md",            # excluded dir
    ".ai-badger/invariants/i.md",
    ".ai-badger/skills/foo/SKILL.md",
    ".ai-badger/skills/foo/references/api.md",
    ".ai-badger/agents/a.md",
    ".ai-badger/instructions/in.md",
    ".ai-badger/config.json",
    ".ai-badger/delegation.md",
    ".ai-badger/copilot-instructions.md",
    ".ai-badger/agent-instructions/agent.md",
    ".ai-badger/state.json",         # excluded exact
    ".ai-badger/task-tracking/t1.json",  # excluded dir
    ".remember/recent.md",
    ".remember/archive.md",
    ".remember/now.md",              # excluded exact
    "README.md",
    "CLAUDE.md",
    "HERMES.md",
    "REVIEW.md",
    "infra/README.md",
    "node_modules/x.js",             # excluded dir
]

EXPECTED_ENUMERATED = [
    (".ai-badger/agent-instructions/agent.md", "agent-model"),
    (".ai-badger/agents/a.md", "agents"),
    (".ai-badger/config.json", "config"),
    (".ai-badger/copilot-instructions.md", "config"),
    (".ai-badger/delegation.md", "config"),
    (".ai-badger/instructions/in.md", "instructions"),
    (".ai-badger/invariants/i.md", "invariants"),
    (".ai-badger/skills/foo/SKILL.md", "skills"),
    (".ai-badger/skills/foo/references/api.md", "skills"),
    (".remember/archive.md", "remember"),
    (".remember/recent.md", "remember"),
    ("CLAUDE.md", "root-md"),
    ("HERMES.md", "root-md"),
    ("README.md", "root-md"),
    ("REVIEW.md", "root-md"),
    ("docs/README.md", "root-md"),
    ("docs/adr/0001-x.md", "adr"),
    ("docs/architecture.md", "architecture"),
    ("docs/explanation/why.md", "explanation"),
    ("docs/how-to/h.md", "howto"),
    ("docs/legacy/l.md", "legacy"),
    ("docs/meta/baseline.json", "meta"),
    ("docs/meta/trust-debt.md", "meta"),
    ("docs/reference/r.md", "reference"),
    ("docs/rules/ru.md", "rules"),
    ("docs/tutorials/t.md", "tutorials"),
    ("infra/README.md", "infra"),
]


def _make_tree(root: Path, files) -> None:
    for rel in files:
        p = root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text("content", encoding="utf-8")
    # enumerate_files iterates .ai-badger/skills unconditionally; the dir exists
    # whenever any skill file does, which all fixtures satisfy.


class TestEnumerateFiles:
    def test_returns_sorted_relative_paths_with_type_keys(self, tmp_path):
        _make_tree(tmp_path, FAKE_TREE_FILES)
        results = enumerate_files(root=tmp_path)
        got = [(rel, type_key) for _, rel, type_key in results]
        assert got == EXPECTED_ENUMERATED

    def test_absolute_paths_point_into_root(self, tmp_path):
        _make_tree(tmp_path, FAKE_TREE_FILES)
        for abs_path, rel, _ in enumerate_files(root=tmp_path):
            assert abs_path == tmp_path / rel

    def test_root_parameterized_two_roots(self, tmp_path):
        root_a = tmp_path / "a"
        root_b = tmp_path / "b"
        _make_tree(root_a, ["docs/adr/0001-x.md", ".ai-badger/skills/s1/SKILL.md", ".remember/recent.md"])
        _make_tree(root_b, ["docs/adr/0002-y.md", "README.md", ".ai-badger/skills/s2/SKILL.md"])
        rels_a = [rel for _, rel, _ in enumerate_files(root=root_a)]
        rels_b = [rel for _, rel, _ in enumerate_files(root=root_b)]
        assert rels_a == [".ai-badger/skills/s1/SKILL.md", ".remember/recent.md", "docs/adr/0001-x.md"]
        assert rels_b == [".ai-badger/skills/s2/SKILL.md", "README.md", "docs/adr/0002-y.md"]
