"""Shared tokenizer for BM25 retrieval (docs/adr/0012-bm25-retrieval-with-a-falsifiable-eval.md).

Splits on non-alphanumerics so token identity is exact rather than substring — a
two-char token like "ts" can never match inside "tests" the way the old
`_KEYWORD_TAG_MAP`'s `keyword in query` check did.
"""
from __future__ import annotations

import re

_SPLIT_RE = re.compile(r"[^a-z0-9]+")
_MIN_TOKEN_LEN = 2
_FOLD_SUFFIXES = ("ing", "ed", "es", "s")
_FOLD_MIN_REMAINDER = 3

# Common English function words, frequent enough in natural-language queries to add no
# retrieval signal — not a stemmer's stopword list. STOPWORD_COUNT must equal len(STOPWORDS).
STOPWORD_COUNT = 109
STOPWORDS = frozenset({
    "a", "an", "the", "and", "or", "but", "if", "then", "else", "of", "to", "in",
    "on", "at", "by", "for", "with", "from", "as", "is", "are", "was", "were",
    "be", "been", "being", "this", "that", "these", "those", "it", "its", "i",
    "you", "he", "she", "we", "they", "them", "him", "her", "his", "our", "your",
    "their", "what", "which", "who", "whom", "when", "where", "why", "how",
    "all", "each", "few", "more", "most", "other", "some", "such", "no", "nor",
    "not", "only", "own", "same", "so", "than", "too", "very", "can", "will",
    "just", "should", "now", "do", "does", "did", "doing", "have", "has", "had",
    "having", "about", "above", "after", "again", "against", "because", "before",
    "below", "between", "both", "during", "further", "here", "there", "once",
    "under", "until", "up", "down", "out", "off", "over", "into", "my", "me",
})


def _fold(token: str) -> str:
    """ies -> y, then strip ing|ed|es|s only when the remainder is >= 3 chars."""
    if token.endswith("ies"):
        remainder = token[:-3]
        return remainder + "y" if len(remainder) >= _FOLD_MIN_REMAINDER else token
    for suffix in _FOLD_SUFFIXES:
        if not token.endswith(suffix):
            continue
        remainder = token[: -len(suffix)]
        return remainder if len(remainder) >= _FOLD_MIN_REMAINDER else token
    return token


def tokenize(text: str) -> list[str]:
    """Lowercase; split on non-alnum; drop stopwords and tokens shorter than 2 chars; fold suffixes."""
    if not text:
        return []
    tokens = []
    for raw in _SPLIT_RE.split(text.lower()):
        if len(raw) < _MIN_TOKEN_LEN or raw in STOPWORDS:
            continue
        tokens.append(_fold(raw))
    return tokens
