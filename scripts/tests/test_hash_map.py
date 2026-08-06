"""Pin CURRENT hash-map contract (scripts/ingest-jsaa-docs.py) — C# tests depend on byte stability."""

import hashlib

from chunking import Chunk
from hash_map import build_hash_map, chunk_written_content, compute_expected_hash

FIXTURE = "## Framework\n\nUses React 18."
GOLDEN_HASH = "3e3b1ab82bd75a244c7881d54c5f2f75163d2256816d655a5b174a38ff3ffbb0"


def _reference_impl(content: str) -> str:
    content_hash = hashlib.sha256(content.encode("utf-8")).hexdigest()
    assigned_path = content_hash + ".md"
    return hashlib.sha256(assigned_path.encode("utf-8") + content.encode("utf-8")).hexdigest()


class TestComputeExpectedHash:
    def test_matches_reference_implementation(self):
        assert compute_expected_hash(FIXTURE) == _reference_impl(FIXTURE)

    def test_golden_value_pinned(self):
        assert compute_expected_hash(FIXTURE) == GOLDEN_HASH

    def test_deterministic_same_content(self):
        assert compute_expected_hash("body text") == compute_expected_hash("body text")

    def test_different_content_different_hash(self):
        assert compute_expected_hash(FIXTURE) != compute_expected_hash(FIXTURE + "x")

    def test_whitespace_sensitive(self):
        assert compute_expected_hash(" a ") != compute_expected_hash("a")


class TestChunkWrittenContent:
    def test_returns_chunk_content_exactly(self):
        chunk = Chunk(structured_path="p", content="  padded  ", context="c", source_file="f")
        assert chunk_written_content(chunk) == "  padded  "

    def test_hash_map_hashes_the_written_content(self):
        chunk = Chunk(structured_path="p", content=FIXTURE, context="c", source_file="f")
        assert build_hash_map([chunk])["p"] == compute_expected_hash(FIXTURE)


class TestBuildHashMap:
    def test_shape_maps_structured_path_to_hash(self):
        chunks = [
            Chunk(structured_path="docs:adr:x.md#decision", content="first", context="c", source_file="x.md"),
            Chunk(structured_path="docs:adr:x.md#context", content="second", context="c", source_file="x.md"),
        ]
        result = build_hash_map(chunks)
        assert list(result) == ["docs:adr:x.md#decision", "docs:adr:x.md#context"]
        assert result["docs:adr:x.md#context"] == compute_expected_hash("second")

    def test_duplicate_structured_path_last_wins(self):
        chunks = [
            Chunk(structured_path="p:same", content="first", context="c", source_file="a.md"),
            Chunk(structured_path="p:other", content="second", context="c", source_file="b.md"),
            Chunk(structured_path="p:same", content="third", context="c", source_file="c.md"),
        ]
        result = build_hash_map(chunks)
        assert list(result) == ["p:same", "p:other"]
        assert result["p:same"] == compute_expected_hash("third")
