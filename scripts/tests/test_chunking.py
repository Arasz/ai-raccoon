"""Pin CURRENT chunking behavior (scripts/ingest-jsaa-docs.py) — byte-identical move."""

from chunking import (
    CHUNKER_MAP,
    Chunk,
    _chunk_path,
    _extract_title,
    _short_rel,
    _source_prefix,
    _split_by_h2,
    chunk_adr,
    chunk_atomic,
    chunk_file,
    chunk_heading,
    chunk_remember,
    chunk_rules,
    chunk_skill,
)

ADR_SHORT = "# 0011 Frontend chassis stack\n\n## Status\n\nAccepted\n\nShort ADR body."

ADR_LONG = (
    "# 0011 Frontend chassis stack\n\n## Context\n\n"
    + "The frontend needs a stack. " * 60
    + "\n\n## Decision (optional)\n\nUse React.\n\n## Consequences\n\nTeam learns React."
)

README_H2 = "# Project\n\nIntro text\n\n## Framework\n\nUses React 18.\n\n## Testing\n\nVitest for unit tests."

SKILL_MD = "---\nname: foo\n---\n# Skill\n\nBody text."

REMEMBER = "# Log\n\nIntro.\n\n## Week of 2026-07-20\n\nDid things.\n\n## 2026-07-27\n\nDid more."

RULES = "# Rules\n\nEvery rule in this table applies.\n\n| ID | Rule |\n|---|---|\n| tdd-mandatory | Tests before code |\n| no-secrets | No hardcoded secrets |"


def _shape(chunks):
    return [(c.structured_path, c.content, c.context, c.source_file) for c in chunks]


class TestSplitByH2:
    def test_no_h2_returns_single_body_chunk(self):
        assert _split_by_h2("plain text\nwith lines") == [("body", "plain text\nwith lines")]

    def test_splits_on_h2_keeping_preamble_and_heading_names(self):
        assert _split_by_h2("# Title\n\nintro\n\n## Section One\n\nbody1\n\n## Section Two\n\nbody2") == [
            ("preamble", "# Title\n\nintro"),
            ("Section One", "body1"),
            ("Section Two", "body2"),
        ]

    def test_empty_section_body_is_kept_as_empty_string(self):
        assert _split_by_h2("## A\n\n## B\n\nbody-b") == [("A", ""), ("B", "body-b")]


class TestExtractTitle:
    def test_extracts_h1(self):
        assert _extract_title("# My Title\n\nbody") == "My Title"

    def test_no_h1_returns_empty(self):
        assert _extract_title("no heading here") == ""


class TestPathHelpers:
    def test_chunk_path_with_section(self):
        assert _chunk_path("docs/adr/0011-x.md", "decision") == "docs:adr:0011-x.md#decision"

    def test_chunk_path_without_section(self):
        assert _chunk_path("README.md", "") == "docs:README.md"

    def test_chunk_path_top_level_docs(self):
        assert _chunk_path("docs/architecture.md", "overview") == "docs:architecture:architecture.md#overview"

    def test_short_rel_strips_docs_subdir(self):
        assert _short_rel("docs/adr/0011-x.md") == "0011-x.md"

    def test_short_rel_strips_top_level_docs(self):
        assert _short_rel("docs/architecture.md") == "architecture.md"

    def test_short_rel_strips_ai_badger_prefix(self):
        assert _short_rel(".ai-badger/invariants/x.md") == "invariants/x.md"

    def test_short_rel_strips_remember_prefix(self):
        assert _short_rel(".remember/recent.md") == "recent.md"

    def test_short_rel_keeps_infra(self):
        assert _short_rel("infra/README.md") == "infra/README.md"

    def test_short_rel_keeps_root_files(self):
        assert _short_rel("README.md") == "README.md"

    def test_source_prefix_docs_subcategories(self):
        assert _source_prefix("docs/adr/x.md") == "docs:adr"
        assert _source_prefix("docs/explanation/x.md") == "docs:explanation"
        assert _source_prefix("docs/how-to/x.md") == "docs:how-to"
        assert _source_prefix("docs/reference/x.md") == "docs:reference"
        assert _source_prefix("docs/rules/x.md") == "docs:rules"
        assert _source_prefix("docs/tutorials/x.md") == "docs:tutorials"
        assert _source_prefix("docs/legacy/x.md") == "docs:legacy"
        assert _source_prefix("docs/meta/x.md") == "docs:meta"

    def test_source_prefix_top_level_docs_is_architecture(self):
        assert _source_prefix("docs/architecture.md") == "docs:architecture"

    def test_source_prefix_other_roots(self):
        assert _source_prefix(".ai-badger/invariants/x.md") == "ai-badger"
        assert _source_prefix(".remember/recent.md") == "remember"
        assert _source_prefix("infra/README.md") == "infra"
        assert _source_prefix("README.md") == "docs"
        assert _source_prefix("other/x.md") == "unknown"


class TestChunkAdr:
    def test_short_adr_is_single_chunk(self):
        assert _shape(chunk_adr("docs/adr/0011-frontend-chassis-stack.md", ADR_SHORT, "adr", "docs:adr")) == [
            (
                "docs:adr:0011-frontend-chassis-stack.md",
                "# 0011 Frontend chassis stack\n\n## Status\n\nAccepted\n\nShort ADR body.",
                "docs:adr",
                "docs/adr/0011-frontend-chassis-stack.md",
            )
        ]

    def test_long_adr_splits_nygard_sections_with_header_chunk(self):
        chunks = chunk_adr("docs/adr/0011-frontend-chassis-stack.md", ADR_LONG, "adr", "docs:adr")
        assert [c.structured_path for c in chunks] == [
            "docs:adr:0011-frontend-chassis-stack.md#header",
            "docs:adr:0011-frontend-chassis-stack.md#context",
            "docs:adr:0011-frontend-chassis-stack.md#decision",
            "docs:adr:0011-frontend-chassis-stack.md#consequences",
        ]
        assert chunks[0].content == "# 0011 Frontend chassis stack\n\n## preamble\n\n# 0011 Frontend chassis stack"
        assert chunks[1].content.startswith("# 0011 Frontend chassis stack\n\n## Context\n\nThe frontend needs a stack.")
        assert chunks[2].content == "# 0011 Frontend chassis stack\n\n## Decision (optional)\n\nUse React."
        assert chunks[3].content == "# 0011 Frontend chassis stack\n\n## Consequences\n\nTeam learns React."


class TestChunkHeading:
    def test_no_sections_falls_back_to_whole_file(self):
        assert _shape(chunk_heading("README.md", "plain body", "root-md", "docs:architecture")) == [
            ("docs:README.md", "plain body", "docs:architecture", "README.md")
        ]

    def test_one_chunk_per_section_with_lowercased_slug(self):
        assert _shape(chunk_heading("README.md", README_H2, "root-md", "docs:architecture")) == [
            ("docs:README.md#preamble", "## preamble\n\n# Project\n\nIntro text", "docs:architecture", "README.md"),
            ("docs:README.md#framework", "## Framework\n\nUses React 18.", "docs:architecture", "README.md"),
            ("docs:README.md#testing", "## Testing\n\nVitest for unit tests.", "docs:architecture", "README.md"),
        ]

    def test_empty_body_sections_are_skipped(self):
        chunks = chunk_heading("README.md", "## A\n\n## B\n\nbody-b", "root-md", "docs:architecture")
        assert [c.structured_path for c in chunks] == ["docs:README.md#b"]


class TestChunkAtomic:
    def test_one_file_one_chunk(self):
        assert _shape(chunk_atomic("docs/meta/trust-debt.md", "  body  ", "meta", "docs:meta")) == [
            ("docs:meta:trust-debt.md", "body", "docs:meta", "docs/meta/trust-debt.md")
        ]


class TestChunkSkill:
    def test_skill_md_is_atomic_with_passed_context(self):
        assert _shape(chunk_skill(".ai-badger/skills/foo/SKILL.md", SKILL_MD, "skills", "ai-badger:skills")) == [
            ("ai-badger:skills/foo/SKILL.md", "---\nname: foo\n---\n# Skill\n\nBody text.", "ai-badger:skills", ".ai-badger/skills/foo/SKILL.md")
        ]

    def test_reference_file_gets_skill_scoped_context(self):
        assert _shape(chunk_skill(".ai-badger/skills/foo/references/api.md", "API reference body.", "skills", "ai-badger:skills")) == [
            ("ai-badger:skills/foo/references/api.md", "API reference body.", "ai-badger:skills:foo:references", ".ai-badger/skills/foo/references/api.md")
        ]


class TestChunkRemember:
    def test_dated_sections_split_with_date_slugs(self):
        assert _shape(chunk_remember(".remember/recent.md", REMEMBER, "remember", "remember:operational")) == [
            ("remember:recent.md#preamble", "# Log\n\nIntro.", "remember:operational", ".remember/recent.md"),
            ("remember:recent.md#2026-07-20", "## Week of 2026-07-20\n\nDid things.", "remember:operational", ".remember/recent.md"),
            ("remember:recent.md#2026-07-27", "## 2026-07-27\n\nDid more.", "remember:operational", ".remember/recent.md"),
        ]

    def test_no_temporal_header_falls_back_to_atomic(self):
        assert _shape(chunk_remember(".remember/recent.md", "# Log\n\n## Some Topic\n\nbody", "remember", "remember:operational")) == [
            ("remember:recent.md", "# Log\n\n## Some Topic\n\nbody", "remember:operational", ".remember/recent.md")
        ]


class TestChunkRules:
    def test_one_chunk_per_table_row_with_preamble(self):
        assert _shape(chunk_rules("docs/rules/engineering.md", RULES, "rules", "docs:rules")) == [
            ("docs:rules:engineering.md#tdd-mandatory", "# Rules\n\nEvery rule in this table applies.\n\nRow 1: | tdd-mandatory | Tests before code |", "docs:rules", "docs/rules/engineering.md"),
            ("docs:rules:engineering.md#no-secrets", "# Rules\n\nEvery rule in this table applies.\n\nRow 2: | no-secrets | No hardcoded secrets |", "docs:rules", "docs/rules/engineering.md"),
        ]

    def test_no_table_falls_back_to_heading(self):
        chunks = chunk_rules("docs/rules/engineering.md", "# Rules\n\nNo table here.", "rules", "docs:rules")
        assert [c.structured_path for c in chunks] == ["docs:rules:engineering.md"]


class TestChunkFile:
    def test_empty_text_returns_no_chunks(self):
        assert chunk_file("README.md", "   \n", "root-md", "docs:architecture") == []

    def test_unknown_type_dispatches_to_atomic(self):
        assert _shape(chunk_file("weird/file.txt", "atomic body", "unknown", "docs:architecture")) == [
            ("unknown:weird/file.txt", "atomic body", "docs:architecture", "weird/file.txt")
        ]

    def test_dispatch_via_chunker_map(self):
        assert CHUNKER_MAP["adr"] is chunk_adr
        assert CHUNKER_MAP["architecture"] is chunk_heading
        assert CHUNKER_MAP["rules"] is chunk_rules
        assert CHUNKER_MAP["skills"] is chunk_skill
        assert CHUNKER_MAP["remember"] is chunk_remember
        assert CHUNKER_MAP["meta"] is chunk_atomic
        assert CHUNKER_MAP["config"] is chunk_atomic

    def test_chunker_map_covers_all_classify_type_keys(self):
        assert set(CHUNKER_MAP) == {
            "adr", "architecture", "explanation", "howto", "reference", "rules",
            "tutorials", "legacy", "meta", "invariants", "skills", "agents",
            "instructions", "config", "agent-model", "remember", "root-md", "infra",
        }

    def test_chunk_dataclass_fields(self):
        c = Chunk(structured_path="p", content="c", context="ctx", source_file="f")
        assert (c.structured_path, c.content, c.context, c.source_file) == ("p", "c", "ctx", "f")
