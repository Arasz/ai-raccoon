"""FTS query-plan port of FtsQueryNormalizer.cs + SourcePathQuery.cs (pure, stdlib-only).

The C# source wins on any conflict with the plan. Differences from C# that are
deliberate and documented:
- Token regex: C# ``[\\p{L}\\p{N}_]+``; here ``\\w+`` with re.UNICODE, which is the
  same class for the ASCII + Latin-1 corpus vocabulary (ASCII letters/digits/_).
  Non-word characters (notably ``-``) split terms, matching C#.
- SourcePathQuery file-part token regex is C# ``[\\w]+`` -> identical ``\\w+`` here.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

_TOKEN_RE = re.compile(r"\w+", re.UNICODE)
_RESERVED = frozenset({"and", "or", "not", "near"})
_STOPWORDS = frozenset(
    {
        "what", "is", "the", "how", "does", "about", "are", "do",
        "can", "should", "will", "would", "could", "has", "have", "been",
        "was", "were", "being", "a", "an", "in", "on", "at", "to",
        "for", "of", "by", "with", "from",
    }
)
_PATH_RE = re.compile(
    r"^(?P<file>[\w./-]+\.(?:md|markdown|txt))(?:#(?P<section>[\w-]+))?$",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class FtsQueryPlan:
    """Port of the FtsQueryPlan record: primary MATCH, OR-fallback, content-token count."""

    expression: str
    fallback: str | None
    token_count: int
    is_path_query: bool = False


def try_build_path_query(query: str) -> tuple[bool, str]:
    """Port of SourcePathQuery.TryBuild: file.md[#section] -> column-scoped AND expression."""
    match = _PATH_RE.match(query.strip())
    if not match:
        return False, ""
    tokens = [
        (f'"{t}"' if t in _RESERVED else t)
        for t in (tok.lower() for tok in _TOKEN_RE.findall(match.group("file")))
    ]
    if match.group("section") is not None:
        tokens.append(f'"{match.group("section").lower()}"')
    if not tokens:
        return False, ""
    columns = "{source_file section}" if match.group("section") is not None else "{source_file}"
    return True, f"{columns} : ({' AND '.join(tokens)})"


def build_plan(query: str) -> FtsQueryPlan:
    """Port of FtsQueryNormalizer.BuildPlan + AsPathQuery (which runs on every search)."""
    raw_tokens = [t for t in (tok.lower() for tok in _TOKEN_RE.findall(query)) if t not in _RESERVED]
    tokens = [t for t in raw_tokens if t not in _STOPWORDS]

    if len(tokens) == 0:
        plan = FtsQueryPlan("", None, 0)
    elif len(tokens) == 1:
        plan = FtsQueryPlan(tokens[0], None, 1)
    else:
        # Bigrams join the fallback iff >= 3 CONTENT tokens (FtsQueryNormalizer L45).
        bigrams = (
            [f'"{tokens[i]} {tokens[i + 1]}"' for i in range(len(tokens) - 1)]
            if len(tokens) >= 3
            else []
        )
        if len(tokens) <= 4:
            plan = FtsQueryPlan(
                " AND ".join(tokens),
                " OR ".join(raw_tokens + bigrams),
                len(tokens),
            )
        else:
            plan = FtsQueryPlan(" OR ".join(raw_tokens), None, len(tokens))

    is_path, path_expression = try_build_path_query(query)
    if is_path:
        plan = FtsQueryPlan(path_expression, None, plan.token_count, is_path_query=True)
    return plan


def should_use_fallback(plan: FtsQueryPlan, primary_count: int, limit: int) -> bool:
    """Port of the SqliteMemoryStore.FtsSearch trigger: fallback iff non-null AND
    primary.Count <= max(TokenCount, query.Limit) — Limit, not the candidate window."""
    return plan.fallback is not None and primary_count <= max(plan.token_count, limit)
