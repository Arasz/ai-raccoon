"""Pin CURRENT benchmark-corpus behavior (scripts/generate-benchmark-corpus.py) — byte-identical move.

Expected values derived by executing the legacy script on these fixtures
(see docs/plans/scripts-refactor.md §5 P6, behavior-pinning derivation).
"""

import os

from benchmark_corpus import (
    MAX_BODY_CHARS,
    OUT,
    REPOS,
    body_excerpt,
    build_queries,
    collect_docs,
    csharp_literal,
    csharp_string,
    emit_cs,
    first_heading,
    safe_id,
    strip_md,
)


class TestConstants:
    def test_hardcoded_paths_preserved_verbatim(self):
        assert REPOS == {
            "jsaa": "/Users/arasz/RiderProjects/job-search-ai-assistant",
            "badger": "/Users/arasz/RiderProjects/ai-badger",
            "home": "/Users/arasz/RiderProjects/arasz-home-page",
        }
        assert OUT == "/Users/arasz/RiderProjects/ai-raccoon/benchmarks/AiRaccoon.Benchmarks/Corpus"
        assert MAX_BODY_CHARS == 420


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


FAKE_REPO_FILES = {
    "jsaa": {
        "docs/adr/0083.md": "# Signal Sources Naming\n\nA decision to rename signal sources consistently across frontend and backend. This ADR records the rename and its rationale for future reference.\n",
        "README.md": "# job-search-ai-assistant\n\nUses GitHub OAuth tokens and PATs for authentication; validates with FluentValidation. Tests follow TDD with a failing test first.\n",
        ".remember/recent.md": "# Recent\n\nNotes about the signal sources rename and CI cost work today. Lots of small details and follow-ups.\n",
    },
    "badger": {
        "docs/adr/0001-skills.md": "# Skill Adoption\n\nRepositories adopt ai-badger skills and execute them via the badger framework rather than storing them idle. Failing tests drive the work.\n",
        "README.md": "# ai-badger\n\nExposes skills to assistants over the Model Context Protocol (MCP); secrets stay out of tracked files via Key Vault.\n",
        ".remember/today-2026-08-01.done.md": "# Today\n\nDrove skill adoption work; fixed a validation bug; noted the event grid cost question for later.\n",
    },
    "home": {
        "docs/adr/0002-identity.md": "# GitHub Identity\n\nProves a GitHub identity belongs to a person during sign-in using OAuth and PAT verification flows.\n",
        "README.md": "# arasz-home-page\n\nPersonal site; chose Event Grid over Service Bus topics and Storage Queue for cost; uses UUID version 7 identifiers.\n",
        ".remember/archive.md": "# Archive\n\nOld notes about durable functions and long-running workflow experiments.\n",
    },
}


def _make_fake_repos(tmp_path):
    repos = {}
    for name, files in FAKE_REPO_FILES.items():
        root = str(tmp_path / name)
        repos[name] = root
        for rel, content in files.items():
            p = os.path.join(root, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "w") as f:
                f.write(content)
    return repos


# id, title, body, source, topics — derived from the legacy script.
EXPECTED_DOCS = [
    ("jsaa-adr-0083", "Signal Sources Naming",
     "Signal Sources Naming A decision to rename signal sources consistently across frontend and backend. This ADR records the rename and its rationale for future reference.",
     "jsaa:docs/adr/0083.md", ["ci-cost"]),  # quirk: "consistently" contains "ci" -> ci-cost keyword
    ("jsaa-readme", "job-search-ai-assistant",
     "job-search-ai-assistant Uses GitHub OAuth tokens and PATs for authentication; validates with FluentValidation. Tests follow TDD with a failing test first.",
     "jsaa:README.md", ["github-auth", "tdd", "validation"]),
    ("badger-adr-0001-skills", "Skill Adoption",
     "Skill Adoption Repositories adopt ai-badger skills and execute them via the badger framework rather than storing them idle. Failing tests drive the work.",
     "badger:docs/adr/0001-skills.md", ["ai-badger", "tdd"]),
    ("badger-doc-readme", "ai-badger",
     "ai-badger Exposes skills to assistants over the Model Context Protocol (MCP); secrets stay out of tracked files via Key Vault.",
     "badger:README.md", ["ai-badger", "mcp", "security"]),
    ("home-adr-0002-identity", "GitHub Identity",
     "GitHub Identity Proves a GitHub identity belongs to a person during sign-in using OAuth and PAT verification flows.",
     "home:docs/adr/0002-identity.md", ["github-auth"]),
    ("home-doc-readme", "arasz-home-page",
     "arasz-home-page Personal site; chose Event Grid over Service Bus topics and Storage Queue for cost; uses UUID version 7 identifiers.",
     "home:README.md", ["ci-cost", "event-grid", "identity"]),
    ("jsaa-remember-recent", "Recent",
     "Recent Notes about the signal sources rename and CI cost work today. Lots of small details and follow-ups.",
     "jsaa:.remember/recent.md", ["ci-cost"]),
    ("badger-remember-today-2026-08-01-done", "Today",
     "Today Drove skill adoption work; fixed a validation bug; noted the event grid cost question for later.",
     "badger:.remember/today-2026-08-01.done.md", ["ci-cost", "event-grid", "validation"]),
    ("home-remember-archive", "Archive",
     "Archive Old notes about durable functions and long-running workflow experiments.",
     "home:.remember/archive.md", ["ci-cost", "durable"]),
]


class TestCollectDocs:
    def test_fake_repos_full_list(self, tmp_path):
        docs = collect_docs(repos=_make_fake_repos(tmp_path))
        got = [(d["id"], d["title"], d["body"], d["source"], d["topics"]) for d in docs]
        assert got == EXPECTED_DOCS

    def test_short_docs_skipped(self, tmp_path):
        # collect_docs requires all three repo keys and lists docs/adr
        # unconditionally (current behavior); short files are skipped.
        repos = {}
        for name in ["jsaa", "badger", "home"]:
            root = str(tmp_path / name)
            repos[name] = root
            os.makedirs(os.path.join(root, "docs/adr"), exist_ok=True)
            p = os.path.join(root, "README.md")
            with open(p, "w") as f:
                f.write("# Tiny\n\nshort")  # < 60 chars -> skipped
        assert collect_docs(repos=repos) == []

    def test_remember_notes_collected(self, tmp_path):
        docs = collect_docs(repos=_make_fake_repos(tmp_path))
        remember = [d["id"] for d in docs if "-remember-" in d["id"]]
        assert remember == [
            "jsaa-remember-recent",
            "badger-remember-today-2026-08-01-done",
            "home-remember-archive",
        ]


EXPECTED_QUERIES = [
    ("doc-badger-adr-0001-skills",
     "What does the project decide or document about: Skill Adoption?",
     ["badger-adr-0001-skills", "badger-doc-readme"],
     "// judgment: query restates the decision/heading of badger-adr-0001-skills; same-topic docs keyword-verified"),
    ("doc-home-adr-0002-identity",
     "What does the project decide or document about: GitHub Identity?",
     ["home-adr-0002-identity", "jsaa-readme"],
     "// judgment: query restates the decision/heading of home-adr-0002-identity; same-topic docs keyword-verified"),
    ("doc-jsaa-adr-0083",
     "What does the project decide or document about: Signal Sources Naming?",
     ["jsaa-adr-0083", "home-doc-readme"],
     "// judgment: query restates the decision/heading of jsaa-adr-0083; same-topic docs keyword-verified"),
    ("cluster-ai-badger",
     "How does a repository adopt and execute ai-badger skills rather than just store them?",
     ["badger-adr-0001-skills", "badger-doc-readme"],
     "// judgment: all docs whose title/body covers 'ai-badger' were keyword-verified"),
    ("cluster-github-auth",
     "How is a GitHub identity proven to belong to a person during sign-in?",
     ["jsaa-readme", "home-adr-0002-identity"],
     "// judgment: all docs whose title/body covers 'github-auth' were keyword-verified"),
    ("cluster-event-grid",
     "Why was Event Grid chosen over Service Bus topics and Storage Queue?",
     ["home-doc-readme"],
     "// judgment: all docs whose title/body covers 'event-grid' were keyword-verified"),
    ("cluster-ci-cost",
     "How is CI spend kept under control in these repositories?",
     ["jsaa-adr-0083", "home-doc-readme"],
     "// judgment: all docs whose title/body covers 'ci-cost' were keyword-verified"),
    ("cluster-validation",
     "Which library is used to keep domain validation rules colocated with the models?",
     ["jsaa-readme"],
     "// judgment: all docs whose title/body covers 'validation' were keyword-verified"),
    ("cluster-identity",
     "What API generates time-ordered unique identifiers?",
     ["home-doc-readme"],
     "// judgment: all docs whose title/body covers 'identity' were keyword-verified"),
    ("cluster-mcp",
     "How does a server expose tools to AI assistants over the Model Context Protocol?",
     ["badger-doc-readme"],
     "// judgment: all docs whose title/body covers 'mcp' were keyword-verified"),
    ("cluster-tdd",
     "What rule governs when production code may be written relative to tests?",
     ["jsaa-readme", "badger-adr-0001-skills"],
     "// judgment: all docs whose title/body covers 'tdd' were keyword-verified"),
    ("cluster-security",
     "How are secrets kept out of tracked files and source control?",
     ["badger-doc-readme"],
     "// judgment: all docs whose title/body covers 'security' were keyword-verified"),
    ("remember-signal-sources",
     "What did the rename that made signal sources consistent across frontend and backend change?",
     ["jsaa-adr-0083"],
     "// judgment: derived from .remember note citing ADR-0083; verified by reading it"),
]


class TestBuildQueries:
    def _docs(self, tmp_path):
        return collect_docs(repos=_make_fake_repos(tmp_path))

    def test_deterministic_count_and_ids(self, tmp_path):
        queries = build_queries(self._docs(tmp_path))
        assert [q[0] for q in queries] == [q[0] for q in EXPECTED_QUERIES]
        assert len(queries) == 13

    def test_full_shape_matches_derived(self, tmp_path):
        assert build_queries(self._docs(tmp_path)) == EXPECTED_QUERIES

    def test_durable_cluster_skipped_when_only_remember_docs(self, tmp_path):
        # home-remember-archive is the only "durable" doc; remember docs are
        # excluded from cluster relevance -> no durable query.
        qids = [q[0] for q in build_queries(self._docs(tmp_path))]
        assert "cluster-durable" not in qids


SMALL_DOCS = [
    {"id": "jsaa-adr-0083", "title": "Signal Sources Naming",
     "body": 'A decision to rename signal sources consistently across frontend and backend. Quotes "and" backslashes \\ live here.',
     "source": "jsaa:docs/adr/0083.md", "topics": []},
    {"id": "jsaa-readme", "title": "job-search-ai-assistant",
     "body": "Uses GitHub OAuth tokens and PATs for authentication; validates with FluentValidation. Tests follow TDD with a failing test first.",
     "source": "jsaa:README.md", "topics": ["github-auth", "tdd", "validation"]},
]

SMALL_QUERIES = [
    ("doc-jsaa-adr-0083", "What does the project decide or document about: Signal Sources Naming?",
     ["jsaa-adr-0083"],
     "// judgment: query restates the decision/heading of jsaa-adr-0083; same-topic docs keyword-verified"),
    ("cluster-github-auth", "How is a GitHub identity proven to belong to a person during sign-in?",
     ["jsaa-readme"],
     "// judgment: all docs whose title/body covers 'github-auth' were keyword-verified"),
]

EXPECTED_CORPUS = "\n".join([
    "// AUTO-GENERATED from the real repositories (job-search-ai-assistant, ai-badger,",
    "// arasz-home-page) + their .remember notes. Regenerate with scripts/generate-benchmark-corpus.py.",
    "// Bodies are verbatim excerpts (2-4 sentences) from the source files.",
    "namespace AiRaccoon.Benchmarks.Corpus;",
    "",
    "/// <summary>Real-world retrieval corpus: docs from the user's actual repositories.</summary>",
    "public static class RealWorldCorpus",
    "{",
    "    public static IReadOnlyList<CorpusDocument> Documents { get; } =",
    "    [",
    '        new("jsaa-adr-0083", "Signal Sources Naming", """',
    'A decision to rename signal sources consistently across frontend and backend. Quotes "and" backslashes \\ live here.',
    '"""),',
    '        new("jsaa-readme", "job-search-ai-assistant", """',
    "Uses GitHub OAuth tokens and PATs for authentication; validates with FluentValidation. Tests follow TDD with a failing test first.",
    '"""),',
    "    ];",
    "}",
    "",
])

EXPECTED_QUERIES_FILE = "\n".join([
    "// AUTO-GENERATED from the real repositories; each query carries a // judgment comment",
    "// documenting how its relevance set was verified.",
    "using AiRaccoon.Benchmarks.Corpus;",
    "",
    "namespace AiRaccoon.Benchmarks.Corpus;",
    "",
    "/// <summary>Real-world queries with honest ground-truth relevance judgments.</summary>",
    "public static class RealWorldQueries",
    "{",
    "    public static IReadOnlyList<CorpusQuery> Queries { get; } =",
    "    [",
    "        // judgment: query restates the decision/heading of jsaa-adr-0083; same-topic docs keyword-verified",
    '        new("doc-jsaa-adr-0083", """',
    "What does the project decide or document about: Signal Sources Naming?",
    '""", ["jsaa-adr-0083"]),',
    "        // judgment: all docs whose title/body covers 'github-auth' were keyword-verified",
    '        new("cluster-github-auth", """',
    "How is a GitHub identity proven to belong to a person during sign-in?",
    '""", ["jsaa-readme"]),',
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
