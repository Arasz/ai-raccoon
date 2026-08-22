"""Pin benchmark-corpus generator behavior (scripts/src/benchmark_corpus.py, ai-raccoon#455).

The corpus source moved from three private/mixed-visibility repositories to this repository's own
public docs, following the ADR-0090 precedent. These tests pin the pure extraction/formatting
helpers (unchanged since the private-source version) and the new single-root collection +
query-derivation behavior.
"""

import os

from benchmark_corpus import (
    CLUSTER_QUESTIONS,
    MAX_BODY_CHARS,
    ROOT_ENV,
    TOPICS,
    body_excerpt,
    build_queries,
    collect_docs,
    csharp_literal,
    csharp_string,
    emit_cs,
    first_heading,
    repo_root,
    safe_id,
    strip_md,
)


class TestConstants:
    def test_no_hardcoded_private_path(self):
        # ai-raccoon#455: benchmark_corpus.py used to hardcode a REPOS dict mapping short keys
        # to three absolute private-checkout paths, plus an absolute OUT path. Neither
        # module-level constant exists any more — the source root resolves at call time via
        # repo_root() (env var or auto-detection).
        import benchmark_corpus

        assert not hasattr(benchmark_corpus, "REPOS")
        assert not hasattr(benchmark_corpus, "OUT")

    def test_max_body_chars_unchanged(self):
        assert MAX_BODY_CHARS == 420

    def test_root_env_var_name(self):
        assert ROOT_ENV == "AIRACCOON_BENCHMARK_CORPUS_ROOT"


class TestRepoRoot:
    def test_env_var_overrides(self, monkeypatch):
        monkeypatch.setenv(ROOT_ENV, "/some/explicit/root")
        assert repo_root() == "/some/explicit/root"

    def test_auto_detects_this_checkout(self, monkeypatch):
        monkeypatch.delenv(ROOT_ENV, raising=False)
        root = repo_root()
        assert os.path.isfile(os.path.join(root, "AiRaccoon.slnx"))


class TestStripMd:
    def test_headings(self):
        assert strip_md("# Heading\n\n## Sub\n\n### Deep") == "Heading Sub Deep"

    def test_links(self):
        assert strip_md("See [the docs](https://example.com/x) for [more](y).") == "See the docs for more."

    def test_inline_code(self):
        assert strip_md("Run `dotnet build` now.") == "Run dotnet build now."

    def test_fences(self):
        # Current behavior: the inline-code regex eats both backtick pairs,
        # leaving the fence content — no dedicated fence handling.
        assert strip_md("```\ncode block\n```") == "code block"

    def test_bullets(self):
        assert strip_md("- one\n* two") == "one two"

    def test_table_and_quote(self):
        assert strip_md("| a | b |\n> quote") == "a b quote"

    def test_whitespace_collapse(self):
        assert strip_md("# T\n\nsome  text   with\nnewlines") == "T some text with newlines"


class TestFirstHeading:
    def test_plain(self):
        assert first_heading("# Real Title\n\nbody", "fallback") == "Real Title"

    def test_skips_lower_headings(self):
        assert first_heading("## Not H1\n\n# The Title\n\nbody", "fallback") == "The Title"

    def test_missing_returns_default(self):
        assert first_heading("no heading here", "fallback") == "fallback"

    def test_long_truncated_at_100(self):
        assert first_heading("# " + "word " * 30, "fallback") == "word word word word word word word word word word word word word word word word word word word word "


class TestBodyExcerpt:
    LONG_SENTENCES = " ".join([
        "The first sentence of the body is comfortably long enough to survive the filter.",
        "The second sentence continues the paragraph with additional detail about the domain.",
        "The third sentence adds yet more context about the decision that was made.",
        "The fourth sentence is long enough to push the running total well past the limit.",
        "The fifth sentence keeps going so the excerpt gets truncated somewhere in here.",
        "The sixth sentence guarantees the running total comfortably exceeds four hundred.",
    ] * 2)
    EXPECTED_LONG = (
        "Long Document The first sentence of the body is comfortably long enough to survive the filter. "
        "The second sentence continues the paragraph with additional detail about the domain. "
        "The third sentence adds yet more context about the decision that was made. "
        "The fourth sentence is long enough to push the running total well past the limit. "
        "The fifth sentence keeps going so the excerpt gets truncated somewhere in here. The"
    )

    def test_long_truncated_at_max(self):
        md = "# Long Document\n\n" + self.LONG_SENTENCES
        excerpt = body_excerpt(md)
        assert len(excerpt) == MAX_BODY_CHARS
        assert excerpt == self.EXPECTED_LONG

    def test_short_kept_whole(self):
        md = "# Short\n\nJust two normal sentences here that are long enough to count. They stay well under the cap."
        assert body_excerpt(md) == (
            "Short Just two normal sentences here that are long enough to count. They stay well under the cap."
        )

    def test_all_short_fragments_fallback(self):
        assert body_excerpt("# T\n\ntiny words only here") == "T tiny words only here"

    def test_heading_only(self):
        assert body_excerpt("# Only A Heading\n\n") == "Only A Heading"


class TestSafeId:
    def test_punctuation_and_case(self):
        assert safe_id("My Cool Doc! (v2)") == "my-cool-doc---v2"

    def test_upper_case(self):
        assert safe_id("UPPER Case") == "upper-case"

    def test_leading_dashes_stripped(self):
        assert safe_id("---leading---") == "leading"

    def test_truncated_at_70(self):
        assert safe_id("a" * 100) == "a" * 70

    def test_path_like_stem(self):
        assert safe_id("docs/adr/0090-public-docs-corpus") == "docs-adr-0090-public-docs-corpus"


class TestCSharpLiteral:
    def test_quotes_use_three_delim(self):
        assert csharp_literal('say "hi" there') == '"""\nsay "hi" there\n"""'

    def test_newlines_kept_raw(self):
        assert csharp_literal("line1\nline2") == '"""\nline1\nline2\n"""'

    def test_delim_boundary_at_500_quotes(self):
        assert csharp_literal('"' * 499).split("\n")[0] == '"""'
        assert csharp_literal('"' * 500).split("\n")[0] == '""""'


class TestCSharpString:
    def test_quotes_escaped(self):
        assert csharp_string('a"b') == '"a\\"b"'

    def test_backslashes_escaped(self):
        assert csharp_string("back\\slash") == '"back\\\\slash"'

    def test_both(self):
        assert csharp_string('both "q" and \\') == '"both \\"q\\" and \\\\"'


# A small fixed doc set standing in for the corpus_config selection — collect_docs's
# relative_paths override skips the real (git-backed) select() call so these tests stay hermetic.
FAKE_ROOT_FILES = {
    "docs/adr/0001-mcp-tools.md":
        "# MCP Tool Surface\n\nThis server exposes its tools to AI assistants over the Model "
        "Context Protocol; every tool maps 1:1 onto the backend API and holds no business logic.\n",
    "docs/adr/0002-tdd.md":
        "# TDD Is Mandatory\n\nA failing, behavior-focused test is written before any production "
        "code change; TDD is not optional here and every PR is checked for it.\n",
    ".ai-badger/invariants/guard-clauses.md":
        "# Guard Clauses Over Hand-Rolled Null Checks\n\nA dedicated guard clause replaces ad hoc "
        "null checks for argument validation, keeping the exception type and message consistent.\n",
    "README.md":
        "# ai-raccoon\n\nAn MCP server exposing agent memory over the Model Context Protocol; "
        "built test-driven with a failing test first, always.\n",
}


def _write_fake_root(tmp_path):
    for rel, content in FAKE_ROOT_FILES.items():
        p = tmp_path / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(content)
    return str(tmp_path), sorted(FAKE_ROOT_FILES)


class TestCollectDocs:
    def test_relative_paths_override_skips_selection(self, tmp_path):
        root, paths = _write_fake_root(tmp_path)
        docs = collect_docs(root=root, relative_paths=paths)
        ids = [d["id"] for d in docs]
        assert ids == sorted(ids)  # sorted by id
        assert set(ids) == {
            "ai-badger-invariants-guard-clauses",
            "docs-adr-0001-mcp-tools",
            "docs-adr-0002-tdd",
            "readme",
        }

    def test_extracts_title_and_body(self, tmp_path):
        root, paths = _write_fake_root(tmp_path)
        docs = collect_docs(root=root, relative_paths=paths)
        by_id = {d["id"]: d for d in docs}
        mcp = by_id["docs-adr-0001-mcp-tools"]
        assert mcp["title"] == "MCP Tool Surface"
        assert mcp["source"] == "docs/adr/0001-mcp-tools.md"
        assert mcp["body"].startswith("MCP Tool Surface This server exposes its tools")

    def test_topics_are_keyword_verified(self, tmp_path):
        root, paths = _write_fake_root(tmp_path)
        docs = collect_docs(root=root, relative_paths=paths)
        by_id = {d["id"]: d for d in docs}
        assert "mcp" in by_id["docs-adr-0001-mcp-tools"]["topics"]
        assert "tdd" in by_id["docs-adr-0002-tdd"]["topics"]
        assert "guard-clauses" in by_id["ai-badger-invariants-guard-clauses"]["topics"]

    def test_short_docs_skipped(self, tmp_path):
        root = str(tmp_path)
        p = tmp_path / "docs" / "adr" / "tiny.md"
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text("# Tiny\n\nshort")  # < 60 chars
        assert collect_docs(root=root, relative_paths=["docs/adr/tiny.md"]) == []

    def test_duplicate_ids_deduplicated(self, tmp_path):
        root = str(tmp_path)
        for rel in ("docs/a/x.md", "docs/b/x.md"):
            p = tmp_path / rel
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text("# X\n\nBody text long enough to survive the sixty character filter easily.\n")
        # both stems resolve to a distinct safe_id (a-x vs b-x) — no collision here, but the
        # mechanism (seen dict, first write wins) is what test_relative_paths_override_skips_selection
        # exercises indirectly; this pins that a genuine collision keeps only the first.
        docs = collect_docs(root=root, relative_paths=["docs/a/x.md", "docs/a/x.md"])
        assert len(docs) == 1

    def test_excludes_docs_that_mention_the_private_repo_by_name(self, tmp_path):
        # Some of this repository's own ADRs (0047/0049/0050/0090) legitimately discuss the
        # removed private corpus by name as historical fact. They must not leak that name into
        # the benchmark corpus (ai-raccoon#455) even though they are otherwise selected.
        root = str(tmp_path)
        p = tmp_path / "docs" / "adr" / "0090-history.md"
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(
            "# The Fixture Corpus Is This Repository's Own Public Docs\n\n"
            "The bank was built from the private " + "job-search-ai-" + "assistant repository, "
            "and it held a great deal of that project's prose before it was removed for good.\n"
        )
        assert collect_docs(root=root, relative_paths=["docs/adr/0090-history.md"]) == []

    def test_defaults_to_real_selection_and_root(self):
        # No relative_paths override: collect_docs must call the real corpus_config selection
        # against the real repo (repo_root() auto-detected). This is the path production uses.
        docs = collect_docs()
        assert len(docs) > 100  # ADR-0090 measured ~199-201 tracked docs in this tree
        ids = {d["id"] for d in docs}
        assert len(ids) == len(docs)  # every id unique


class TestBuildQueries:
    def _docs(self, tmp_path):
        root, paths = _write_fake_root(tmp_path)
        return collect_docs(root=root, relative_paths=paths)

    def test_doc_derived_queries_sample_every_third(self, tmp_path):
        docs = self._docs(tmp_path)
        queries = build_queries(docs)
        doc_queries = [q for q in queries if q[0].startswith("doc-")]
        expected_ids = [f"doc-{d['id']}" for d in docs[::3]]
        assert [q[0] for q in doc_queries] == expected_ids

    def test_doc_query_relevance_includes_self(self, tmp_path):
        docs = self._docs(tmp_path)
        queries = build_queries(docs)
        by_id = {q[0]: q for q in queries}
        # Every 3rd doc (by sorted id) is sampled; with this 4-doc fixture that is index 0
        # (ai-badger-invariants-guard-clauses) and index 3 (readme).
        sampled_id = docs[0]["id"]
        guard_query = by_id[f"doc-{sampled_id}"]
        assert sampled_id in guard_query[2]

    def test_cluster_query_present_when_topic_matched(self, tmp_path):
        docs = self._docs(tmp_path)
        queries = build_queries(docs)
        qids = {q[0] for q in queries}
        assert "cluster-mcp" in qids
        assert "cluster-tdd" in qids
        assert "cluster-guard-clauses" in qids

    def test_cluster_query_absent_when_topic_unmatched(self, tmp_path):
        # The fake fixture never mentions Key Vault / secrets, so "security" has no relevant doc.
        docs = self._docs(tmp_path)
        queries = build_queries(docs)
        qids = {q[0] for q in queries}
        assert "cluster-security" not in qids

    def test_every_relevant_id_exists_in_docs(self, tmp_path):
        docs = self._docs(tmp_path)
        ids = {d["id"] for d in docs}
        queries = build_queries(docs)
        for _, _, relevant, _ in queries:
            for r in relevant:
                assert r in ids

    def test_cluster_topics_all_have_a_question(self):
        assert set(TOPICS) == set(CLUSTER_QUESTIONS)


SMALL_DOCS = [
    {"id": "docs-adr-0001-mcp-tools", "title": "MCP Tool Surface",
     'body': 'Quotes "and" backslashes \\ live here, describing the MCP tool surface.',
     "source": "docs/adr/0001-mcp-tools.md", "topics": ["mcp"]},
    {"id": "readme", "title": "ai-raccoon",
     "body": "An MCP server exposing agent memory over the Model Context Protocol.",
     "source": "README.md", "topics": ["mcp"]},
]

SMALL_QUERIES = [
    ("doc-docs-adr-0001-mcp-tools", "What does the project decide or document about: MCP Tool Surface?",
     ["docs-adr-0001-mcp-tools"],
     "// judgment: query restates the decision/heading of docs-adr-0001-mcp-tools; same-topic docs keyword-verified"),
    ("cluster-mcp", "How does this project expose tools to AI assistants over the Model Context Protocol?",
     ["docs-adr-0001-mcp-tools", "readme"],
     "// judgment: all docs whose title/body covers 'mcp' were keyword-verified"),
]

EXPECTED_CORPUS = "\n".join([
    "// AUTO-GENERATED from this repository's own public docs (ai-raccoon#455, ADR-0090).",
    "// Regenerate with scripts/generate-benchmark-corpus.py.",
    "// Bodies are verbatim excerpts (2-4 sentences) from the source files.",
    "namespace AiRaccoon.Benchmarks.Corpus;",
    "",
    "/// <summary>Real-world retrieval corpus: this repository's own public documentation.</summary>",
    "public static class RealWorldCorpus",
    "{",
    "    public static IReadOnlyList<CorpusDocument> Documents { get; } =",
    "    [",
    '        new("docs-adr-0001-mcp-tools", "MCP Tool Surface", """',
    'Quotes "and" backslashes \\ live here, describing the MCP tool surface.',
    '"""),',
    '        new("readme", "ai-raccoon", """',
    "An MCP server exposing agent memory over the Model Context Protocol.",
    '"""),',
    "    ];",
    "}",
    "",
])

EXPECTED_QUERIES_FILE = "\n".join([
    "// AUTO-GENERATED from this repository's own public docs; each query carries a",
    "// // judgment comment documenting how its relevance set was verified.",
    "using AiRaccoon.Benchmarks.Corpus;",
    "",
    "namespace AiRaccoon.Benchmarks.Corpus;",
    "",
    "/// <summary>Real-world queries with honest ground-truth relevance judgments.</summary>",
    "public static class RealWorldQueries",
    "{",
    "    public static IReadOnlyList<CorpusQuery> Queries { get; } =",
    "    [",
    "        // judgment: query restates the decision/heading of docs-adr-0001-mcp-tools; same-topic docs keyword-verified",
    '        new("doc-docs-adr-0001-mcp-tools", """',
    "What does the project decide or document about: MCP Tool Surface?",
    '""", ["docs-adr-0001-mcp-tools"]),',
    "        // judgment: all docs whose title/body covers 'mcp' were keyword-verified",
    '        new("cluster-mcp", """',
    "How does this project expose tools to AI assistants over the Model Context Protocol?",
    '""", ["docs-adr-0001-mcp-tools", "readme"]),',
    "    ];",
    "}",
    "",
])


class TestEmitCs:
    def test_returns_doc_and_query_counts(self, tmp_path):
        assert emit_cs(SMALL_DOCS, SMALL_QUERIES, out=str(tmp_path)) == (2, 2)

    def test_writes_expected_corpus_file(self, tmp_path):
        emit_cs(SMALL_DOCS, SMALL_QUERIES, out=str(tmp_path))
        with open(os.path.join(str(tmp_path), "RealWorldCorpus.cs")) as f:
            assert f.read() == EXPECTED_CORPUS

    def test_writes_expected_queries_file(self, tmp_path):
        emit_cs(SMALL_DOCS, SMALL_QUERIES, out=str(tmp_path))
        with open(os.path.join(str(tmp_path), "RealWorldQueries.cs")) as f:
            assert f.read() == EXPECTED_QUERIES_FILE

    def test_creates_out_dir(self, tmp_path):
        nested = str(tmp_path / "a" / "b")
        emit_cs(SMALL_DOCS, SMALL_QUERIES, out=nested)
        assert os.path.isfile(os.path.join(nested, "RealWorldCorpus.cs"))

    def test_out_defaults_to_repo_root_benchmarks_corpus(self, monkeypatch, tmp_path):
        monkeypatch.setenv(ROOT_ENV, str(tmp_path))
        emit_cs(SMALL_DOCS, SMALL_QUERIES)
        expected = tmp_path / "benchmarks" / "AiRaccoon.Benchmarks" / "Corpus" / "RealWorldCorpus.cs"
        assert expected.is_file()
