"""Validator for the multi-language code eval corpus (plan §E1) and its
query set (plan §E2).

`validate_manifest` checks a loaded MANIFEST.json against the files it
describes: per-language/per-band file counts, declared band vs. measured
non-blank lines, sha256 integrity, a LICENSES file per external family, an
allowed license, and the held-out family count (ADR-0056 split).

`split_identifier`, `comment_lines`, `validate_queries` and `assign_split`
support E2's query set: `split_identifier` is the reference implementation
the C# `IdentifierSplitter` (plan P2-B) must match byte-for-byte.
"""

from __future__ import annotations

import hashlib
import re
from collections import Counter, defaultdict
from pathlib import Path

from retrieval_tuning import repo_data

_SEED = repo_data.CORPORA["code-eval-corpus"]

CODE_CORPUS_DIR = "scripts/retrieval_tuning/code-corpus"

SIZE_BANDS: dict[str, tuple[int, int]] = {band: tuple(bounds) for band, bounds in _SEED["sizeBands"].items()}

FILES_PER_BAND = 3
BANDS_PER_LANGUAGE = len(SIZE_BANDS)
FILES_PER_LANGUAGE = FILES_PER_BAND * BANDS_PER_LANGUAGE

ALLOWED_LICENSES = set(_SEED["allowedLicenses"])

MIN_HELD_OUT_FAMILIES = 3

SELF_FAMILY = "self"


def _resolve_file_path(row: dict, root: Path) -> Path:
    """A row's bytes: its frozen copy under `files/`."""
    return root / row["vendoredPath"]


def _check_snapshots(files: list[dict], problems: list[str]) -> bool:
    """Every row, self included, must be a frozen copy under files/; a live path drifts on edit."""
    prefix = f"{CODE_CORPUS_DIR}/files/"
    ok = True
    for row in files:
        vendored = row.get("vendoredPath") or ""
        if not vendored.startswith(prefix):
            problems.append(f"{row['path']}: vendoredPath must be a snapshot under {prefix} (got {vendored!r})")
            ok = False
    return ok


def _count_nonblank_lines(path: Path) -> int:
    text = path.read_text(encoding="utf-8", errors="replace")
    return sum(1 for line in text.splitlines() if line.strip())


def _sha256_of(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _check_band_counts(manifest: dict, files: list[dict], problems: list[str]) -> None:
    """Every declared (or observed) language needs exactly 3 src files per band."""
    src_rows = [row for row in files if row.get("role", "src") == "src"]
    by_language: dict[str, Counter] = {}
    for row in src_rows:
        by_language.setdefault(row["language"], Counter())[row.get("sizeBand")] += 1

    expected_languages = set(manifest.get("languages", [])) | set(by_language)
    for language in sorted(expected_languages):
        counts = by_language.get(language, Counter())
        total = sum(counts.values())
        if total != FILES_PER_LANGUAGE:
            problems.append(
                f"{language}: expected {FILES_PER_LANGUAGE} src files, found {total}"
            )
        for band in SIZE_BANDS:
            found = counts.get(band, 0)
            if found != FILES_PER_BAND:
                problems.append(
                    f"{language}/{band}: expected {FILES_PER_BAND} src files, found {found}"
                )
        unexpected = set(counts) - set(SIZE_BANDS) - {None}
        for band in sorted(unexpected):
            problems.append(f"{language}: unknown sizeBand '{band}'")


def _check_band_matches_measured_lines(files: list[dict], root: Path, problems: list[str]) -> None:
    for row in files:
        if row.get("role", "src") != "src":
            continue
        band = row.get("sizeBand")
        path = _resolve_file_path(row, root)
        if not path.exists():
            problems.append(f"{row['path']}: vendored file is missing ({path})")
            continue
        if band not in SIZE_BANDS:
            continue
        lo, hi = SIZE_BANDS[band]
        try:
            measured = _count_nonblank_lines(path)
        except OSError as exc:
            problems.append(f"{row['path']}: could not read file to measure lines ({exc})")
            continue
        if not (lo <= measured <= hi):
            problems.append(
                f"{row['path']}: sizeBand '{band}' expects {lo}-{hi} non-blank lines, "
                f"measured {measured}"
            )
        recorded = row.get("lines")
        if recorded is not None and recorded != measured:
            problems.append(
                f"{row['path']}: manifest 'lines' ({recorded}) disagrees with measured "
                f"non-blank lines ({measured})"
            )


def _check_sha256(files: list[dict], root: Path, problems: list[str]) -> None:
    for row in files:
        path = _resolve_file_path(row, root)
        if not path.exists():
            continue  # already reported by the band/line check
        expected = row.get("sha256")
        if not expected:
            problems.append(f"{row['path']}: manifest row is missing sha256")
            continue
        actual = _sha256_of(path)
        if actual.lower() != str(expected).lower():
            problems.append(
                f"{row['path']}: sha256 mismatch (manifest {expected}, actual {actual})"
            )


def _check_licenses(files: list[dict], root: Path, problems: list[str]) -> None:
    external_families = sorted({
        row["family"] for row in files if row.get("repo") != SELF_FAMILY and row["family"] != SELF_FAMILY
    })
    licenses_dir = root / CODE_CORPUS_DIR / "LICENSES"
    for family in external_families:
        license_path = licenses_dir / family
        if not license_path.is_file():
            problems.append(f"{family}: missing LICENSES file ({license_path})")


def _check_license_enum(files: list[dict], problems: list[str]) -> None:
    seen: dict[str, str] = {}
    for row in files:
        license_ = row.get("license")
        if license_ not in ALLOWED_LICENSES:
            key = f"{row['family']}:{row['path']}"
            if key not in seen:
                problems.append(
                    f"{row['path']}: license '{license_}' is not one of {sorted(ALLOWED_LICENSES)}"
                )
                seen[key] = license_


def _check_held_out_families(manifest: dict, files: list[dict], problems: list[str]) -> None:
    declared = set(manifest.get("heldOutFamilies", []))
    external_families = {
        row["family"] for row in files if row.get("repo") != SELF_FAMILY and row["family"] != SELF_FAMILY
    }
    valid = declared & external_families
    if len(valid) < MIN_HELD_OUT_FAMILIES:
        problems.append(
            f"heldOutFamilies: expected at least {MIN_HELD_OUT_FAMILIES} external families, "
            f"found {len(valid)} ({sorted(valid)})"
        )


def _check_self_held_out(manifest: dict, files: list[dict], problems: list[str]) -> None:
    """Every selfHeldOut pin must name a self row; a stale pin silently empties the held-out split."""
    self_paths = {row["path"] for row in files if row["family"] == SELF_FAMILY}
    for path in manifest.get("selfHeldOut", []):
        if path not in self_paths:
            problems.append(f"selfHeldOut: {path} is not a self row in the corpus")


def validate_manifest(manifest: dict, root: Path) -> list[str]:
    """Structural + integrity validation of a loaded MANIFEST.json.

    `root` is the repository root: vendored files resolve at
    `root/<vendoredPath>` (self-family files too, snapshotted under files/self/), and
    per-family LICENSES files live under `root/<CODE_CORPUS_DIR>/LICENSES/`.
    Returns a list of problems (empty = valid).
    """
    root = Path(root)
    files = manifest.get("files", [])
    problems: list[str] = []

    if not files:
        problems.append("manifest has no files")
        return problems

    if not _check_snapshots(files, problems):
        return problems
    _check_band_counts(manifest, files, problems)
    _check_band_matches_measured_lines(files, root, problems)
    _check_sha256(files, root, problems)
    _check_licenses(files, root, problems)
    _check_license_enum(files, problems)
    _check_held_out_families(manifest, files, problems)
    _check_self_held_out(manifest, files, problems)

    return problems

# ---------------------------------------------------------------------------
# E2: identifier splitting (the reference the C# IdentifierSplitter matches)
# ---------------------------------------------------------------------------

_SEPARATORS_RE = re.compile(r"[_\-.\s]+")
_LOWER_TO_UPPER_RE = re.compile(r"([a-z])([A-Z])")
_ACRONYM_TO_WORD_RE = re.compile(r"([A-Z]+)([A-Z][a-z])")
_LETTER_TO_DIGIT_RE = re.compile(r"([A-Za-z])([0-9])")
_DIGIT_TO_LETTER_RE = re.compile(r"([0-9])([A-Za-z])")
_BOUNDARY = "\x00"


def _mark_boundary(pattern: re.Pattern, text: str) -> str:
    return pattern.sub(lambda m: m.group(1) + _BOUNDARY + m.group(2), text)


def _split_case_and_digits(chunk: str) -> list[str]:
    marked = chunk
    for pattern in (_LOWER_TO_UPPER_RE, _ACRONYM_TO_WORD_RE, _LETTER_TO_DIGIT_RE, _DIGIT_TO_LETTER_RE):
        marked = _mark_boundary(pattern, marked)
    return [part for part in marked.split(_BOUNDARY) if part]


def split_identifier(name: str) -> list[str]:
    """Split one identifier into lowercase word parts (plan P2-B reference).

    Boundaries: `_`, `-`, `.` and whitespace; a lowercase-to-uppercase
    transition; an acronym run followed by a new capitalised word
    (`HTTPServer` -> `HTTP`, `Server`); and letter/digit transitions
    (`Server2` -> `Server`, `2`). The C# `IdentifierSplitter` must match this
    output byte-for-byte.
    """
    if not name:
        return []
    tokens: list[str] = []
    for chunk in _SEPARATORS_RE.split(name):
        if chunk:
            tokens.extend(_split_case_and_digits(chunk))
    return [tok.lower() for tok in tokens if tok]


# ---------------------------------------------------------------------------
# E2: comment/docstring line extraction (leak-gate support)
# ---------------------------------------------------------------------------

_LINE_COMMENT_LANGUAGES = {"C", "C++", "C#", "JS", "TS", "JSX", "Rust", "Go"}
_BLOCK_COMMENT_LANGUAGES = {"C", "C++", "C#", "JS", "TS", "JSX", "Rust", "Go", "CSS"}
_HASH_COMMENT_LANGUAGES = {"Python"}
_DOUBLE_DASH_LANGUAGES = {"SQL"}
_HTML_COMMENT_LANGUAGES = {"HTML"}
_PY_DOCSTRING_LANGUAGES = {"Python"}
_PY_DOCSTRING_DELIMS = ('"""', "'''")


def comment_lines(text: str, language: str) -> list[str]:
    """Comment/docstring lines of one source file (good enough, not a parser).

    Covers `//` and `///`, `/* */`, `#`, `--`, `<!-- -->` and Python
    triple-quoted strings, dispatched per `language` so a CSS `#id` selector
    or a C preprocessor line is never mistaken for a comment.
    """
    result: list[str] = []
    in_block = False
    block_close: str | None = None
    in_doc = False
    doc_delim: str | None = None

    for line in text.splitlines():
        stripped = line.strip()

        if in_doc:
            result.append(line)
            if doc_delim in stripped:
                in_doc = False
            continue

        if in_block:
            result.append(line)
            if block_close in stripped:
                in_block = False
            continue

        if language in _PY_DOCSTRING_LANGUAGES:
            opened = False
            for delim in _PY_DOCSTRING_DELIMS:
                if delim in stripped:
                    result.append(line)
                    if stripped.count(delim) < 2:
                        in_doc = True
                        doc_delim = delim
                    opened = True
                    break
            if opened:
                continue

        if language in _HASH_COMMENT_LANGUAGES and stripped.startswith("#"):
            result.append(line)
            continue

        if language in _DOUBLE_DASH_LANGUAGES and stripped.startswith("--"):
            result.append(line)
            continue

        if language in _HTML_COMMENT_LANGUAGES and "<!--" in stripped:
            result.append(line)
            after = stripped.split("<!--", 1)[1]
            if "-->" not in after:
                in_block = True
                block_close = "-->"
            continue

        if language in _BLOCK_COMMENT_LANGUAGES and "/*" in stripped:
            result.append(line)
            after = stripped.split("/*", 1)[1]
            if "*/" not in after:
                in_block = True
                block_close = "*/"
            continue

        if language in _LINE_COMMENT_LANGUAGES and stripped.startswith("//"):
            result.append(line)
            continue

    return result


# ---------------------------------------------------------------------------
# E2: query-set validation
# ---------------------------------------------------------------------------

VALID_QUERY_CATEGORIES = {
    "behaviour-nl",
    "identifier-fragment",
    "path-context",
    "distractor",
    "test-intent",
    "negative",
}

_DIFFICULTIES = {"easy", "medium", "hard"}

_REQUIRED_QUERY_KEYS = {
    "id", "category", "query", "kind", "language", "sizeBand", "family",
    "expectedSource", "expectedLines", "negativeTest", "searchLimit",
    "targetProjectId", "targetScope", "relevanceGrade", "expectedHash", "difficulty",
}

MIN_NEGATIVE_QUERIES = 20
MIN_PAIRS_COVERED_PER_HELD_OUT_FAMILY = 2
LEAK_JACCARD_THRESHOLD = 0.4

_WORD_RE = re.compile(r"[a-z0-9]+")
_IDENTIFIER_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")


def _leak_tokens(text: str) -> set[str]:
    """Lowercase alnum words of length > 2 (the leak gate's tokenisation)."""
    return {w for w in _WORD_RE.findall(text.lower()) if len(w) > 2}


def _jaccard(a: set[str], b: set[str]) -> float:
    if not a or not b:
        return 0.0
    return len(a & b) / len(a | b)


def _ordered_words(text: str) -> list[str]:
    """Ordered lowercase alnum words, no length filter (circularity guard)."""
    return _WORD_RE.findall(text.lower())


def _source_key(family, path) -> str:
    return f"{family}/{path}"


def validate_queries(entries: list[dict], manifest: dict, root: Path) -> list[str]:
    """Structural, leak-gate, circularity and coverage validation of an
    assembled E2 query set against its MANIFEST.json (plan §E2).

    `root` resolves each manifest row's `vendoredPath`, the same convention
    as `validate_manifest`. Returns a list of problems (empty = valid).
    """
    root = Path(root)
    problems: list[str] = []
    files = manifest.get("files", [])

    source_index: dict[str, list[dict]] = defaultdict(list)
    for row in files:
        source_index[_source_key(row["family"], row["path"])].append(row)

    text_cache: dict[str, str] = {}

    def _read(vendored_path: str) -> str:
        if vendored_path not in text_cache:
            text_cache[vendored_path] = (root / vendored_path).read_text(
                encoding="utf-8", errors="replace"
            )
        return text_cache[vendored_path]

    seen_ids: set = set()
    for entry in entries:
        entry_id = entry.get("id")
        label = entry_id if entry_id is not None else "?"

        missing = _REQUIRED_QUERY_KEYS - entry.keys()
        if missing:
            problems.append(f"{label}: missing required field(s) {sorted(missing)}")

        if entry_id is None:
            problems.append("entry missing 'id'")
        elif entry_id in seen_ids:
            problems.append(f"duplicate id '{entry_id}'")
        else:
            seen_ids.add(entry_id)

        category = entry.get("category")
        if category not in VALID_QUERY_CATEGORIES:
            problems.append(
                f"{label}: invalid category {category!r} "
                f"(expected one of {sorted(VALID_QUERY_CATEGORIES)})"
            )

        if not entry.get("query"):
            problems.append(f"{label}: missing 'query'")

        if entry.get("kind") != "code":
            problems.append(f"{label}: 'kind' must be 'code' (got {entry.get('kind')!r})")

        if not isinstance(entry.get("negativeTest"), bool):
            problems.append(f"{label}: 'negativeTest' must be a bool")

        difficulty = entry.get("difficulty")
        if difficulty is not None and difficulty not in _DIFFICULTIES:
            problems.append(
                f"{label}: invalid difficulty {difficulty!r} (expected one of {sorted(_DIFFICULTIES)})"
            )

        expected_source = entry.get("expectedSource")
        expected_lines = entry.get("expectedLines")

        if category == "negative":
            if expected_source is not None:
                problems.append(f"{label}: negative query must have expectedSource null")
            if expected_lines is not None:
                problems.append(f"{label}: negative query must have expectedLines null")
            if entry.get("negativeTest") is not True:
                problems.append(f"{label}: negative query must set negativeTest true")
            continue

        if entry.get("negativeTest"):
            problems.append(f"{label}: non-negative query must not set negativeTest true")

        if not expected_source:
            problems.append(f"{label}: missing expectedSource")
            continue

        matches = source_index.get(expected_source, [])
        if len(matches) != 1:
            problems.append(
                f"{label}: expectedSource {expected_source!r} resolves to "
                f"{len(matches)} manifest row(s) (expected exactly 1)"
            )
            continue

        row = matches[0]
        try:
            text = _read(row["vendoredPath"])
        except OSError as exc:
            problems.append(f"{label}: could not read target file ({exc})")
            continue
        total_lines = len(text.splitlines())

        if not (isinstance(expected_lines, (list, tuple)) and len(expected_lines) == 2):
            problems.append(f"{label}: expectedLines must be [start, end]")
        else:
            start, end = expected_lines
            if not (isinstance(start, int) and isinstance(end, int)):
                problems.append(f"{label}: expectedLines must be integers")
            elif start < 1 or end > total_lines or start > end:
                problems.append(
                    f"{label}: expectedLines [{start}, {end}] out of bounds for "
                    f"{row['path']} ({total_lines} lines) or start > end"
                )

        comments = comment_lines(text, row.get("language"))
        query_leak_tokens = _leak_tokens(entry.get("query", ""))
        for comment in comments:
            score = _jaccard(query_leak_tokens, _leak_tokens(comment))
            if score > LEAK_JACCARD_THRESHOLD:
                problems.append(
                    f"{label}: leak gate — query overlaps a comment/docstring line "
                    f"of {row['path']} (jaccard={score:.2f}): {comment.strip()!r}"
                )
                break

        if category == "identifier-fragment":
            query_words = _ordered_words(entry.get("query", ""))
            for identifier in set(_IDENTIFIER_RE.findall(text)):
                if split_identifier(identifier) == query_words:
                    problems.append(
                        f"{label}: circular — query equals the full split of identifier "
                        f"{identifier!r} in {row['path']} (use a partial fragment or paraphrase)"
                    )
                    break

    # Category counts per file (src files, then src/test pairs).
    for row in files:
        if row.get("role", "src") != "src":
            continue
        key = _source_key(row["family"], row["path"])
        behaviour = sum(1 for e in entries if e.get("category") == "behaviour-nl" and e.get("expectedSource") == key)
        if behaviour != 1:
            problems.append(f"{row['path']}: expected 1 behaviour-nl query, found {behaviour}")
        identifier = sum(1 for e in entries if e.get("category") == "identifier-fragment" and e.get("expectedSource") == key)
        if identifier != 1:
            problems.append(f"{row['path']}: expected 1 identifier-fragment query, found {identifier}")
        if row.get("sizeBand") in ("medium", "large"):
            path_context = sum(1 for e in entries if e.get("category") == "path-context" and e.get("expectedSource") == key)
            if path_context != 1:
                problems.append(f"{row['path']}: expected 1 path-context query (medium/large), found {path_context}")

    pairs_by_family: dict[str, list[dict]] = defaultdict(list)
    for row in files:
        if row.get("role") == "test" and row.get("pairOf"):
            pairs_by_family[row["family"]].append(row)
            src_key = _source_key(row["family"], row["pairOf"])
            test_key = _source_key(row["family"], row["path"])
            distractor = sum(1 for e in entries if e.get("category") == "distractor" and e.get("expectedSource") == src_key)
            if distractor != 2:
                problems.append(
                    f"{row['path']}: expected 2 distractor queries on paired src {row['pairOf']!r}, found {distractor}"
                )
            test_intent = sum(1 for e in entries if e.get("category") == "test-intent" and e.get("expectedSource") == test_key)
            if test_intent != 1:
                problems.append(f"{row['path']}: expected 1 test-intent query, found {test_intent}")

    negatives = sum(1 for e in entries if e.get("category") == "negative")
    if negatives < MIN_NEGATIVE_QUERIES:
        problems.append(f"expected at least {MIN_NEGATIVE_QUERIES} negative queries, found {negatives}")

    # Held-out coverage (ADR-0056): external families only, "self" excluded.
    held_out_families = {f for f in manifest.get("heldOutFamilies", []) if f != SELF_FAMILY}
    present_held_out = {e.get("family") for e in entries if e.get("family") in held_out_families}
    if len(present_held_out) < MIN_HELD_OUT_FAMILIES:
        problems.append(
            f"held-out coverage: expected queries from at least {MIN_HELD_OUT_FAMILIES} "
            f"held-out families, found {len(present_held_out)} ({sorted(present_held_out)})"
        )

    for family in sorted(held_out_families):
        pairs = pairs_by_family.get(family, [])
        if not pairs:
            continue
        covered = sum(
            1 for row in pairs
            if any(
                e.get("category") == "test-intent"
                and e.get("expectedSource") == _source_key(family, row["path"])
                for e in entries
            )
        )
        if covered < MIN_PAIRS_COVERED_PER_HELD_OUT_FAMILY:
            problems.append(
                f"{family}: held-out family with pairs needs >= "
                f"{MIN_PAIRS_COVERED_PER_HELD_OUT_FAMILY} pairs' queries, found {covered}"
            )

    return problems


# ---------------------------------------------------------------------------
# E2: tuning/held-out split assignment (ADR-0056)
# ---------------------------------------------------------------------------

def assign_split(entry: dict, seed: dict) -> str:
    """"held-out" when the entry's family is in `seed["heldOutFamilies"]`, or
    it's a `self`-family entry targeting a path pinned in
    `seed["selfHeldOut"]`; "tuning" otherwise (ADR-0056)."""
    family = entry.get("family")
    held_out_families = set(seed.get("heldOutFamilies") or [])
    if family in held_out_families:
        return "held-out"

    if family == SELF_FAMILY:
        self_held_out = set(seed.get("selfHeldOut") or [])
        source = entry.get("expectedSource") or ""
        prefix = f"{SELF_FAMILY}/"
        path = source[len(prefix):] if source.startswith(prefix) else None
        if path in self_held_out:
            return "held-out"

    return "tuning"
