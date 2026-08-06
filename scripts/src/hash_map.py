"""Chunk-hash-map contract (moved verbatim from scripts/ingest-jsaa-docs.py).

C# integration tests depend on byte-stable hashes: scripts/chunk-hash-map.json
is consumed by 7 test files. Do not change the hash construction.
"""

from __future__ import annotations

import hashlib

from chunking import Chunk


def compute_expected_hash(content: str) -> str:
    """Compute AiRaccoon's content hash.

    AiRaccoon computes:
      assigned_path = SHA256(content).hex() + ".md"
      expected_hash = SHA256(UTF8(assigned_path) + UTF8(content)).hex()
    """
    content_hash = hashlib.sha256(content.encode("utf-8")).hexdigest()
    assigned_path = content_hash + ".md"
    return hashlib.sha256(assigned_path.encode("utf-8") + content.encode("utf-8")).hexdigest()


def chunk_written_content(chunk: Chunk) -> str:
    """The actual content sent to AiRaccoon — plain chunk body since Wave 2 (plan C §3 2d)."""
    return chunk.content


def build_hash_map(chunks: list[Chunk]) -> dict[str, str]:
    """Build {structured_path: expected_hash} for all chunks, using written content."""
    return {chunk.structured_path: compute_expected_hash(chunk_written_content(chunk)) for chunk in chunks}
