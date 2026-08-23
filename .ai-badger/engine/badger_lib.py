"""Shared helpers for ai-badger scripts.

Deterministic and offline (Python 3.10+, the floor CI tests): scripts must be runnable wherever
the plugin is
installed. `ensure_root(allow_network=True)` is the single exception and the only function
here that may reach the network; it is opt-in and pinned to a release tag. JSON Schema
validation uses the audited `jsonschema` library (see engine/requirements.txt) rather than
a hand-rolled validator.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path, PurePosixPath
from typing import Any, Dict, Iterable, List, NamedTuple, Optional, Set, Tuple

sys.path.insert(0, str(Path(__file__).resolve().parent))
import frontmatter as fm  # noqa: E402


class FeatureType(NamedTuple):
    """One catalog feature type and the behaviour every stage keys off.

    ``index_rule`` names index_build's discovery rule; ``drift_reports_new`` marks types
    whose catalog items drift reports when the manifest lacks them — only safe where
    scaffold records an entry under the item's own index name, or the report never clears.
    ``hashes_source`` marks types whose written output is not a copy of its source, so the
    manifest must carry the source hash — drift.compare re-hashes the source (ADR-0006).
    """

    name: str
    index_rule: str
    drift_reports_new: bool
    hashes_source: bool = False

    @property
    def md_carrying(self) -> bool:
        """True when items are `*.md` files under the feature dir, named by stem."""
        return self.index_rule == "md"


FEATURE_TYPES: Tuple[FeatureType, ...] = (
    FeatureType("skills", "skills", True),
    FeatureType("personas", "md", True),
    FeatureType("invariants", "md", True),
    FeatureType("instructions", "md", True),
    # These three are materialised under names of their own — a rendered/seeded output, a
    # settings.json wiring, a written file per adjustment — so no manifest entry is ever
    # keyed by the index item's name and a "new" report could never clear (ADR-0006).
    FeatureType("templates", "templates", False, hashes_source=True),
    FeatureType("hooks", "hooks", False),
    FeatureType("adjustments", "adjustments", False, hashes_source=True),
    # An mcp item describes a server; nothing per-item is written into the scaffold at all —
    # its content reaches a project through document slots and generated agent config. So no
    # manifest entry is keyed by an mcp item's name either, for a different reason than the
    # three above (ADR-0014).
    FeatureType("mcp", "mcp", False),
)

FEATURES = [ft.name for ft in FEATURE_TYPES]

DRIFT_NEW_FEATURES: Tuple[str, ...] = tuple(
    ft.name for ft in FEATURE_TYPES if ft.drift_reports_new
)

_BY_NAME = {ft.name: ft for ft in FEATURE_TYPES}

# The feature types a project may decline by index name in `config.exclude`. Same predicate
# as drift's "new" report for the same reason: only these are recorded under the item's own
# name, so only here does a name in config address one delivered artifact.
EXCLUDABLE_FEATURES: Tuple[str, ...] = DRIFT_NEW_FEATURES

# The feature types a project may ask for by index name in `config.include`. Narrower than
# EXCLUDABLE_FEATURES on purpose: only skills carry a scope that withholds them until asked
# for (ADR-0005). The object shape leaves room for a second entry without a schema break.
INCLUDABLE_FEATURES: Tuple[str, ...] = ("skills",)


def feature_type(name: str) -> FeatureType:
    """Look up a feature type by name; raises KeyError for anything not in the registry."""
    return _BY_NAME[name]


def exclusions(config: Dict[str, Any]) -> Dict[str, Set[str]]:
    """Names `config.exclude` declines, keyed by feature type — every key always present.

    Tolerant of a malformed block on purpose: drift reads configs this library did not
    validate, and a refusal there would convert a bad edit into a broken refresh.
    """
    declared = config.get("exclude")
    if not isinstance(declared, dict):
        declared = {}
    return {
        feature: {n for n in declared.get(feature) or [] if isinstance(n, str)}
        for feature in EXCLUDABLE_FEATURES
    }


# A skill citing `../<sibling>/references/<file>.md` depends on that sibling being installed.
# The lookbehind rejects `../../…`, which reaches past the skills directory and is not a sibling.
SIBLING_REFERENCE_RE = re.compile(r"(?<![./])\.\./([a-z][a-z0-9-]*)/references/[a-z-]+\.md")

# Skills that cannot do their job alone. Naming any member — or the group — installs all of them.
#
# Grouping protects cross-references between skills that cite each other's references/ in place.
SKILL_GROUPS: Dict[str, Tuple[str, ...]] = {
    "documentation": ("scaffold-documentation", "update-documentation", "migrate-documentation"),
    # One ruleset, two default skills: design-tests writes, review-tests judges; both read
    # review-tests/references/ (SYNTHESIS.md ruling A). Grouping protects cross-citations.
    "testing": ("design-tests", "review-tests"),
}


def expand_skill_groups(names: Iterable[str]) -> Set[str]:
    """`names` with every group they touch expanded to all its members.

    A member's name pulls in its siblings; the group's own name works too, so a project can ask
    for `documentation` and get the three. A name in no group passes through untouched — an
    unknown name is `inclusion_notes`' to report, and two places reporting it disagree eventually.
    """
    wanted: Set[str] = set()
    for name in names:
        if name in SKILL_GROUPS:
            wanted.update(SKILL_GROUPS[name])
            continue
        wanted.add(name)
        for members in SKILL_GROUPS.values():
            if name in members:
                wanted.update(members)
    return wanted


def inclusions(config: Dict[str, Any]) -> Dict[str, Set[str]]:
    """Names `config.include` asks for, keyed by feature type — every key always present.

    Mirrors `exclusions`, including its tolerance of a malformed block: a refusal here would
    turn a bad edit into a broken refresh rather than a reported one.
    """
    declared = config.get("include")
    if not isinstance(declared, dict):
        declared = {}
    return {
        feature: {n for n in declared.get(feature) or [] if isinstance(n, str)}
        for feature in INCLUDABLE_FEATURES
    }


# Canonical agent list — keep in sync with schemas/agents.schema.json and
# schemas/config.schema.json agents enum.
AGENT_NAMES = ["claude", "copilot", "hermes"]


# ---------------------------------------------------------------------- breaking versions
def read_breaking_versions(root: Path) -> List[str]:
    """Read BREAKING_VERSIONS file — one semver per line, comments start with #."""
    bv = root / "BREAKING_VERSIONS"
    if not bv.exists():
        return []
    versions = []
    for line in bv.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            versions.append(line)
    return versions


def _parse_semver(v: str) -> tuple:
    """Parse 'major.minor.patch' into (major, minor, patch) ints."""
    parts = v.split(".")
    return tuple(int(p) for p in parts[:3])


def is_breaking_transition(from_version: str, to_version: str, root: Path) -> bool:
    """Check if the version transition crosses a breaking version boundary.

    A transition from_version -> to_version is breaking if any version in
    BREAKING_VERSIONS satisfies from_version < breaking <= to_version.
    """
    breaking = read_breaking_versions(root)
    if not breaking:
        return False
    try:
        from_v = _parse_semver(from_version)
        to_v = _parse_semver(to_version)
    except (ValueError, IndexError):
        return False
    for bv in breaking:
        try:
            bv_v = _parse_semver(bv)
        except (ValueError, IndexError):
            continue
        if from_v < bv_v <= to_v:
            return True
    return False


# ------------------------------------------------------------------- copy skew (Shape D)
COPY_SKEW_OK = "ok"
COPY_SKEW_WARN = "warn"
COPY_SKEW_REFUSE = "refuse"


def copy_skew(copies_dir: Path, root: Path) -> Tuple[str, Optional[str]]:
    """Judge install-time plugin copies against the framework root they resolve.

    Returns `(verdict, message)`. Skew is material — `COPY_SKEW_REFUSE` — when a
    BREAKING_VERSIONS entry lies between the two versions in either direction; a downgrade
    across a boundary is as dangerous as an upgrade. Anything absent, unreadable or
    unorderable is not judged, because absence is not evidence of staleness.
    """
    copies_dir, root = Path(copies_dir), Path(root)
    try:
        record = json.loads(
            (copies_dir / ".ai-badger" / "manifest.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return COPY_SKEW_OK, None
    recorded = record.get("copiedFromVersion") if isinstance(record, dict) else None
    if not isinstance(recorded, str) or not recorded:
        return COPY_SKEW_OK, None
    try:
        current = (root / "VERSION").read_text(encoding="utf-8").strip()
    except OSError:
        return COPY_SKEW_OK, None
    if not current or current == recorded:
        return COPY_SKEW_OK, None
    try:
        low, high = sorted((recorded, current), key=_parse_semver)
    except (ValueError, IndexError):
        return COPY_SKEW_OK, None
    message = (f"the Hermes plugin copies in {copies_dir} were installed from ai-badger "
               f"{recorded}, but {root} is now {current} — re-run welcome-ai-badger to "
               f"refresh them")
    if is_breaking_transition(low, high, root):
        return COPY_SKEW_REFUSE, message
    return COPY_SKEW_WARN, message


# --------------------------------------------------------------------------- roots / io
FRAMEWORK_REPO = "https://github.com/Arasz/ai-badger"
FRAMEWORK_CACHE = Path.home() / ".ai-badger" / "framework"
RELEASE_TAG_PREFIX = "ai-badger--v"
ROOT_ENV_VAR = "AI_BADGER"
SCAFFOLD_DIR = ".ai-badger"
BACKUP_DIR_NAME = ".ai-badger.bckp"
MANIFEST_NAME = "manifest.json"
MANIFEST_ROOT_KEY = "frameworkRoot"
MANIFEST_VERSION_KEY = "frameworkVersion"


class FrameworkRootNotFound(RuntimeError):
    """No usable ai-badger framework root, and none may be fetched without consent."""


def is_framework_root(path: Path) -> bool:
    """The one predicate: a framework root holds schemas/, features/ and engine/badger_lib.py.

    Stated here once. The bootstrap shims repeat it verbatim because they run before this
    module can be imported — that is the bootstrap problem, not a second definition (ADR-0007).
    """
    return ((path / "schemas").is_dir() and (path / "features").is_dir()
            and (path / "engine" / "badger_lib.py").is_file())


def _manifest_candidates(start: Path) -> List[Path]:
    """Every .ai-badger/manifest.json at or above `start`, nearest first."""
    found = []
    for anc in [start, *start.parents]:
        if anc.name == SCAFFOLD_DIR:
            found.append(anc / MANIFEST_NAME)
        found.append(anc / SCAFFOLD_DIR / MANIFEST_NAME)
    return found


def recorded_root(start: Path) -> Optional[Path]:
    """Framework root recorded in the nearest readable manifest above `start`, or None.

    The pointer a copied file otherwise lacks. `start` is always the script's own location,
    never the working directory: only whoever installed the script may steer its sys.path
    (ADR-0009 decision 6). Validated before it is returned.
    """
    for manifest in _manifest_candidates(start):
        if not manifest.is_file():
            continue
        try:
            recorded = load_json(manifest).get(MANIFEST_ROOT_KEY)
        except (OSError, ValueError):
            continue
        if not recorded:
            continue
        candidate = Path(recorded).expanduser()
        if not candidate.is_absolute():
            candidate = manifest.parent.parent / candidate
        if is_framework_root(candidate):
            return candidate.resolve()
    return None


def recorded_version(start: Path) -> Optional[str]:
    """Framework version recorded in the nearest readable manifest above `start`, or None.

    What the caller was installed at, which is what a resolved cache has to agree with.
    """
    for manifest in _manifest_candidates(start):
        if not manifest.is_file():
            continue
        try:
            recorded = load_json(manifest).get(MANIFEST_VERSION_KEY)
        except (OSError, ValueError, AttributeError):
            continue
        if recorded:
            return str(recorded)
    return None


def warn_on_cache_skew(root: Path, start: Path) -> None:
    """Say so when the cache answered with an engine older than the caller it is serving.

    Last in the resolution order and never updated in place, so it can be many releases
    behind (ADR-0009). A warning, not a refusal: discovery inputs never raise, and the same
    statement runs inside session-start hooks. Silent unless both versions are known.
    """
    cache = FRAMEWORK_CACHE
    if root.resolve() != cache.resolve():
        return
    try:
        have = (cache / "VERSION").read_text(encoding="utf-8").strip()
    except OSError:
        return
    want = recorded_version(start)
    if have and want and have != want:
        print(f"ai-badger: {cache} is version {have}, but this project was scaffolded "
              f"by {want}. The cache is never updated in place — remove it, or pass "
              f"--root <framework checkout>.", file=sys.stderr)


def _declared_root(value, source: str) -> Path:
    """Accept an operator-supplied root, or refuse loudly: a wrong pointer is not a fallback."""
    candidate = Path(value).expanduser()
    if not is_framework_root(candidate):
        raise FrameworkRootNotFound(
            f"{source} is {candidate}, which is not an ai-badger framework root "
            f"(no schemas/ + features/ + engine/badger_lib.py)."
        )
    return candidate.resolve()


def resolve_framework_root(explicit=None, start: Optional[Path] = None) -> Path:
    """Resolve the ai-badger framework root. Pure lookup: no network, ever.

    Ordered inputs, first hit wins. Every input is derived from the script's own location or
    from an operator, never from the working directory (ADR-0009 decision 6):

    1. `explicit` — a `--root` argument.
    2. an ancestor walk from `start` (default: this file).
    3. `$AI_BADGER` — the checkout documented in getting-started.md Route B; refuses rather
       than falls through when it names a non-root.
    4. `frameworkRoot` recorded in the nearest `.ai-badger/manifest.json` above `start`.
    5. `~/.ai-badger/framework`, the cache — which reports its own version skew when it wins.

    Four deployment shapes (ADR-0007): a framework checkout and the Claude plugin cache are
    answered by (2); a `.ai-badger/` scaffold and `~/.hermes/plugins/` hold no framework
    above them, so (2) structurally cannot succeed there and (4) is what answers them.
    """
    if explicit:
        return _declared_root(explicit, "--root")

    origin = (start or Path(__file__)).resolve()
    for anc in [origin, *origin.parents]:
        if is_framework_root(anc):
            return anc

    env_value = os.environ.get(ROOT_ENV_VAR)
    if env_value:
        return _declared_root(env_value, f"${ROOT_ENV_VAR}")

    recorded = recorded_root(origin)
    if recorded:
        return recorded

    if is_framework_root(FRAMEWORK_CACHE):
        warn_on_cache_skew(FRAMEWORK_CACHE, origin)
        return FRAMEWORK_CACHE

    raise FrameworkRootNotFound(
        f"ai-badger framework root not found above {origin}, in ${ROOT_ENV_VAR}, in any "
        f"{SCAFFOLD_DIR}/{MANIFEST_NAME} {MANIFEST_ROOT_KEY}, or at {FRAMEWORK_CACHE}. "
        f"Pass --root <framework checkout>, or call ensure_root(allow_network=True) to fetch "
        f"the release matching your installed VERSION from {FRAMEWORK_REPO}."
    )


def find_root(start: Optional[Path] = None) -> Path:
    """Resolve the framework root — the long-standing name for `resolve_framework_root`."""
    return resolve_framework_root(start=start)


class MissingVersion(RuntimeError):
    """A tree that must declare a VERSION has none, or an unreadable/empty one."""


def read_version(root: Path) -> str:
    """The version `root` declares in its VERSION file. Raises MissingVersion when it has none.

    For callers that require the marker; `installed_version` degrades to None instead.
    """
    version_file = root / "VERSION"
    try:
        version = version_file.read_text(encoding="utf-8").strip()
    except (OSError, UnicodeDecodeError) as exc:
        detail = getattr(exc, "strerror", None) or exc
        raise MissingVersion(f"{version_file}: cannot be read ({detail})") from exc
    if not version:
        raise MissingVersion(f"{version_file}: empty — it must name the version of this tree")
    return version


def installed_version(start: Optional[Path] = None) -> Optional[str]:
    """Read the VERSION file of the tree this code is installed in, or None."""
    p = (start or Path(__file__)).resolve()
    for anc in [p, *p.parents]:
        version_file = anc / "VERSION"
        if version_file.is_file():
            text = version_file.read_text(encoding="utf-8").strip()
            if text:
                return text
    return None


def ensure_root(start: Optional[Path] = None, allow_network: bool = False,
                version: Optional[str] = None) -> Path:
    """Find the framework root, optionally fetching the pinned release if none is present.

    Network access is opt-in and pinned: the clone targets the tag matching `version`
    (default: the installed VERSION), never an unpinned branch. See ADR-0001 decision 2.
    """
    try:
        return find_root(start)
    except FrameworkRootNotFound:
        if not allow_network:
            raise

    release = version or installed_version(start)
    if not release:
        raise FrameworkRootNotFound(
            "cannot fetch the framework: no release version is known (no VERSION file "
            "above the installed scripts). Pass version=<x.y.z> or --root <checkout>."
        )
    return _clone_pinned(release)


def _clone_pinned(version: str) -> Path:
    """Clone the framework at tag ai-badger--v{version} into FRAMEWORK_CACHE."""
    if FRAMEWORK_CACHE.exists():
        raise FrameworkRootNotFound(
            f"{FRAMEWORK_CACHE} exists but is not a usable framework root (no schemas/ + "
            f"features/). It is never updated in place — inspect it, remove it, and retry."
        )

    tag = f"{RELEASE_TAG_PREFIX}{version}"
    FRAMEWORK_CACHE.parent.mkdir(parents=True, exist_ok=True)
    try:
        result = subprocess.run(
            ["git", "clone", "--depth=1", "--branch", tag, FRAMEWORK_REPO,
             str(FRAMEWORK_CACHE)],
            capture_output=True, text=True, timeout=120, check=False,
        )
    except (subprocess.TimeoutExpired, OSError) as exc:
        raise FrameworkRootNotFound(f"git clone of {tag} failed: {exc}") from exc

    if result.returncode != 0:
        raise FrameworkRootNotFound(
            f"failed to clone {FRAMEWORK_REPO} at {tag}: {result.stderr.strip()}. "
            f"Releases before 0.20.0 carry no tag."
        )
    if not is_framework_root(FRAMEWORK_CACHE):
        raise FrameworkRootNotFound(
            f"cloned {tag} into {FRAMEWORK_CACHE} but it is not a framework root "
            f"(no schemas/ + features/ + engine/badger_lib.py)"
        )
    return FRAMEWORK_CACHE


# Variables git exports to the hooks it runs, each of which pins a git invocation to a
# repository the caller never named. A hook that shells out with `git -C <dir>` inherits them
# and gets the hook's repository with <dir> read as its work-tree root — so under a worktree
# commit every .gitignore above <dir> became invisible (docs/changelog/0.95.0-*.md).
GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                    "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")


def git_env(env: Optional[Dict[str, str]] = None) -> Dict[str, str]:
    """`env` (default `os.environ`) minus every variable that pins git to another repository."""
    out = dict(os.environ if env is None else env)
    for name in GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


def run_git(args: List[str], cwd: Path, **kwargs):
    """Run `git -C cwd <args>`, letting `cwd` alone decide which repository answers.

    The one place this repo invokes git: repository discovery must never come from the
    environment, because a gate's whole job is to report on the tree it was pointed at.
    """
    kwargs.setdefault("capture_output", True)
    kwargs.setdefault("text", True)
    return subprocess.run(["git", "-C", str(cwd), *args], env=git_env(),
                          check=kwargs.pop("check", False), **kwargs)


def load_json(path: Path) -> Any:
    """Read and parse a JSON file."""
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def atomic_write_text(path: Path, text: str) -> None:
    """Write `text` via a temp file in the same directory + os.replace, preserving mode.

    An interrupted write leaves the previous content intact and no temp file behind.
    `config_guard._atomic_write` is the same contract for scripts that must load without
    badger_lib on the path.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    mode = path.stat().st_mode & 0o7777 if path.exists() else None
    handle, tmp = tempfile.mkstemp(dir=str(path.parent), prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as fh:
            fh.write(text)
        if mode is not None:
            os.chmod(tmp, mode)
        os.replace(tmp, str(path))
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)


def dump_json(path: Path, data: Any) -> None:
    """Write `data` atomically as pretty-printed, newline-terminated JSON."""
    atomic_write_text(path, json.dumps(data, indent=2, ensure_ascii=False) + "\n")


def sha256_text(text: str) -> str:
    """Return the hex SHA-256 digest of `text`."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


# Keys a scaffold rewrites or that only point at a schema: not the project's declaration.
CONFIG_HASH_IGNORED = ("$schema", "frameworkVersion")


def config_hash(config: Dict[str, Any]) -> str:
    """SHA-256 of a config's declarations, canonicalized so formatting is not drift."""
    subset = {k: v for k, v in config.items() if k not in CONFIG_HASH_IGNORED}
    return sha256_text(json.dumps(subset, sort_keys=True, separators=(",", ":"),
                                  ensure_ascii=False))


def sha256_file(path: Path) -> str:
    """Return the hex SHA-256 digest of a file's bytes, or of a dir's tree (name + content)."""
    h = hashlib.sha256()
    if path.is_dir():
        for f in sorted(path.rglob("*")):
            if f.is_file():
                h.update(f.relative_to(path).as_posix().encode("utf-8"))
                h.update(f.read_bytes())
    else:
        h.update(path.read_bytes())
    return h.hexdigest()


# Build artefacts and OS droppings: never authored, never a contribution, wherever they appear.
# `.DS_Store` is here because an OS dropping is not an edit, and a skill that hashed one
# would report as locally modified until someone deleted a file they cannot see (#224).
ARTEFACT_EXCLUDE_PATTERNS = ["__pycache__", "*.pyc", ".DS_Store"]

# Adds the skill-authoring conventions, matching scaffold.py's _test_ignore — tests and evals
# sit beside a skill and are not shipped. Those names are ordinary content anywhere else, so
# only skill trees may exclude them (#224).
SKILL_EXCLUDE_PATTERNS = ["tests", "test_*.py", "*_test.py",
                          "evals"] + ARTEFACT_EXCLUDE_PATTERNS


def _matches_exclude(name: str, patterns: List[str]) -> bool:
    """Check if a name matches any of the exclude glob patterns."""
    import fnmatch
    return any(fnmatch.fnmatch(name, p) for p in patterns)


def excluded_by_patterns(rel: str, patterns: List[str]) -> bool:
    """True when any segment of a relative path matches one of the exclude patterns."""
    return any(_matches_exclude(part, patterns) for part in PurePosixPath(rel).parts)


def nested_entry_targets(entries: List[Dict[str, Any]], target: str) -> List[str]:
    """Targets other manifest entries own strictly inside `target`, relative to it.

    A directory entry must be hashed over the files it owns, not over everything that ends
    up in its directory: adjustments write into a skill's own tree and carry their own
    entries, so counting them makes the recorded hash unmatchable forever (#224).
    """
    prefix = target.rstrip("/") + "/"
    nested = {t[len(prefix):] for t in (e.get("target") for e in entries)
              if isinstance(t, str) and t.startswith(prefix)}
    return sorted(nested)


def dir_content_hash(path: Path, exclude: Optional[List[str]] = None,
                     exclude_rel: Optional[Iterable[str]] = None) -> Dict[str, Any]:
    """Compute a structural fingerprint + content hash for a directory.

    Two-phase approach for efficiency:
    1. Structural: file_count + dir_count (cheap O(n) walk)
    2. Content: SHA-256 of sorted (relative_path + file_content) for each file

    Files/dirs matching `exclude` glob patterns are skipped entirely. `exclude_rel` skips
    exact relative paths and their subtrees, for the cases where a name is not enough —
    one file another manifest entry owns, not every file that shares its name (#224).
    `dir_count` still counts every surviving directory: once another entry owns part of the
    tree the number describes a different tree than the recorded one, so the caller stops
    comparing it rather than this trying to reconstruct it (#230).

    Returns:
        {"file_count": int, "dir_count": int, "content_hash": str}
    """
    if not path.is_dir():
        raise ValueError(f"Not a directory: {path}")

    exclude = exclude or []
    # `""` and `"."` both name this directory, which would make every path a descendant of an
    # excluded one and reduce the whole fingerprint to the hash of nothing. An entry whose
    # target *is* this directory owns no path inside it, so dropping it is the honest reading.
    excluded_paths = {p for p in (PurePosixPath(x) for x in (exclude_rel or ()))
                      if p != PurePosixPath(".")}
    h = hashlib.sha256()
    file_count = 0
    dir_count = 0

    # PurePosixPath throughout: a platform Path never compares equal to one on Windows.
    walked = [(item, PurePosixPath(item.relative_to(path).as_posix()))
              for item in sorted(path.rglob("*"))]

    def skipped(rel: PurePosixPath) -> bool:
        """Excluded by a name pattern, or owned by another entry — itself or via an ancestor."""
        return (excluded_by_patterns(str(rel), exclude)
                or rel in excluded_paths
                or any(p in excluded_paths for p in rel.parents))

    for item, rel in walked:
        if skipped(rel):
            continue

        if item.is_dir():
            dir_count += 1
        elif item.is_file():
            file_count += 1
            h.update(str(rel).encode("utf-8"))
            h.update(item.read_bytes())

    return {
        "file_count": file_count,
        "dir_count": dir_count,
        "content_hash": h.hexdigest(),
    }


# -------------------------------------------------------------- validation (jsonschema)
def _jsonschema():
    """The `jsonschema` module, imported on first use.

    Required, never optional: the ImportError propagates so validation refuses rather than
    silently passing. Deferred because 11 of 13 entry points never validate (ADR-0011, D1).
    """
    import jsonschema  # pylint: disable=import-outside-toplevel
    return jsonschema


def _loc(err) -> str:
    path = "$" + "".join(f"[{p!r}]" if isinstance(p, int) else f".{p}" for p in err.absolute_path)
    return path


def validate(instance: Any, schema: Dict[str, Any]) -> List[str]:
    """Return a sorted list of human-readable validation errors (empty == valid)."""
    validator = _jsonschema().Draft202012Validator(schema)
    errors = sorted(validator.iter_errors(instance), key=lambda e: list(e.absolute_path))
    return [f"{_loc(e)}: {e.message}" for e in errors]


def validate_file(instance_path: Path, schema_path: Path) -> List[str]:
    """Load both JSON files and validate the instance against the schema."""
    return validate(load_json(instance_path), load_json(schema_path))


def check_schemas_selfvalid(schemas_dir: Path) -> List[str]:
    """Meta-check: every *.schema.json is itself a valid Draft 2020-12 schema."""
    jsonschema = _jsonschema()
    problems: List[str] = []
    for sp in sorted(schemas_dir.glob("*.schema.json")):
        try:
            jsonschema.Draft202012Validator.check_schema(load_json(sp))
        except jsonschema.exceptions.SchemaError as exc:  # pragma: no cover
            problems.append(f"{sp.name}: {exc.message}")
    return problems


# ------------------------------------------------------------------------ skill routing
SKILL_SCOPE_DEFAULT = "default"
SKILL_SCOPE_OPT_IN = "optIn"

SKILL_SCOPE_VALUES = (SKILL_SCOPE_DEFAULT, SKILL_SCOPE_OPT_IN)


def skill_scope_in(skill_dir: Path) -> Optional[str]:
    """The `scope:` a skill declares in its own SKILL.md frontmatter, or None (ADR-0018).

    None covers "no key", "unreadable" and "not one of the two values" alike; skills_lint
    rule 12 is what turns any of them into a failure, at the point of authorship.
    """
    try:
        text = (skill_dir / "SKILL.md").read_text(encoding="utf-8")
    except (OSError, ValueError, UnicodeDecodeError):
        return None
    entry = fm.split(text).entry("scope")
    if entry is None:
        return None
    declared = entry.value().strip("'\"")
    return declared if declared in SKILL_SCOPE_VALUES else None


def _skills_scoped(skills_dir: Path, scope: str) -> List[str]:
    """Skill directories under `skills_dir` whose SKILL.md declares `scope`, sorted."""
    if not skills_dir.is_dir():
        return []
    return sorted(d.name for d in skills_dir.iterdir()
                  if d.is_dir() and skill_scope_in(d) == scope)


def default_skills_in(skills_dir: Path) -> List[str]:
    """Skills in `skills_dir` declaring `scope: default`, sorted — what ships unasked."""
    return _skills_scoped(skills_dir, SKILL_SCOPE_DEFAULT)


def opt_in_skills_in(skills_dir: Path) -> List[str]:
    """Skills in `skills_dir` declaring `scope: optIn`, sorted — the catalog a project may name."""
    return _skills_scoped(skills_dir, SKILL_SCOPE_OPT_IN)


# What a skill with no readable description is reported as. Reporting only — never a value
# any behaviour keys off.
NO_DESCRIPTION = "(no description)"


def skill_description(skill_md: Path) -> Optional[str]:
    """The `description:` scalar from a SKILL.md's frontmatter, or None when it cannot be read.

    Reporting only, so it degrades to None on any parse miss instead of raising. An inline
    scalar is unquoted here and nowhere else: this is the one caller that renders the value
    to a human rather than handing it on.
    """
    try:
        text = skill_md.read_text(encoding="utf-8")
    except (OSError, ValueError, UnicodeDecodeError):
        return None
    entry = fm.split(text).entry("description")
    if entry is None:
        return None
    if entry.inline in fm.BLOCK_INDICATORS:
        return entry.value() or None
    return entry.inline.strip("'\"") or None


def inclusion_notes(included: Iterable[str], excluded: Iterable[str],
                    addable: Iterable[str], defaults: Iterable[str]) -> List[str]:
    """One note per name in `config.include.skills`, saying what it added or why it could not.

    Never fatal, for the reason exclusions are not: refresh refuses on an invalid config, so a
    fatal note would turn an upstream deletion or a scope change into a broken upgrade.
    """
    declined, offerable, ships = set(excluded), set(addable), set(defaults)
    notes = []
    for name in sorted(set(included)):
        if name in SKILL_GROUPS:
            members = ", ".join(SKILL_GROUPS[name])
            notes.append(f"included skill group '{name}' — delivered {members} "
                         f"(they read each other's references/ and cannot work alone)")
        elif name in declined:
            notes.append(f"inclusion '{name}' is also in config.exclude.skills — "
                         f"exclude wins; not delivered")
        elif name in offerable:
            notes.append(f"included optIn skill '{name}' (config.include.skills)")
        elif name in ships:
            notes.append(f"inclusion '{name}' is already a default skill — "
                         f"safe to remove from config.json")
        else:
            notes.append(f"inclusion '{name}' matches no optIn catalog skill — "
                         f"safe to remove from config.json")
    return notes


def available_opt_in(root: Path, installed: Iterable[str]) -> List[Dict[str, str]]:
    """Every opt-in catalog skill this project has not installed, with the edit that adds it.

    Report-only: `name`, a one-line `description` read from the skill's own SKILL.md, and the
    literal `config.json` edit a reader can paste.
    """
    have = set(installed or ())
    skills_dir = root / "features" / "common" / "skills"
    return [
        {
            "name": name,
            "description": skill_description(skills_dir / name / "SKILL.md") or NO_DESCRIPTION,
            "configEdit": '"include": {"skills": ["%s"]}' % name,
        }
        for name in opt_in_skills_in(skills_dir) if name not in have
    ]


def scaffolded_skill_names(manifest: Dict[str, Any]) -> List[str]:
    """Skill names a manifest records as scaffolded, ignoring per-file provenance rows.

    A row like `<skill>/extensions/<agent>/extension.md` is provenance for a skill already
    named by its own row, not a distinct skill. This is the one home for that rule.
    """
    if not isinstance(manifest, dict):
        return []
    entries = manifest.get("entries")
    if not isinstance(entries, list):
        return []
    return [e["name"] for e in entries
            if isinstance(e, dict) and e.get("feature") == "skills"
            and isinstance(e.get("name"), str) and "/" not in e["name"]]


def stack_local_skills(skills_dir: Path) -> List[str]:
    """Every skill a stack directory holds — a stack ships its whole catalog (ADR-0010).

    Never the common catalog, whose skills declare a `scope:` instead: `skills_for_stack` and
    `SkillDelivery.discover_stack_local` are the two callers, and both route `common` away
    from here. Passing it one anyway returns its optIn skills, which ship only when asked for.
    """
    if not skills_dir.is_dir():
        return []
    return sorted(
        d.name for d in skills_dir.iterdir()
        if d.is_dir() and (d / "SKILL.md").exists()
    )


def skills_for_stack(root: Path, stack: str) -> List[str]:
    """Shippable skills for one stack, combining universal defaults and stack-local.

    For the common stack: the skills whose frontmatter declares `scope: default`.
    For any other stack: everything the stack's directory holds.
    This is the single place both scaffold.py and sync_plugin_skills.py derive from.
    """
    skills_dir = root / "features" / stack / "skills"
    if stack in DEFAULT_COMMON_STACKS:
        return default_skills_in(skills_dir)
    return stack_local_skills(skills_dir)


def catalog_skills_for_stack(root: Path, stack: str) -> List[str]:
    """Every skill a stack's directory holds, whatever its scope.

    `skills_for_stack` answers "what does this stack ship by default", which is the right
    question when deciding what to deliver. It is the wrong question when deciding which
    already-delivered skills belong to an agent: an `optIn` skill a project asked for is in
    the delivered set and not in that answer, so filtering through it dropped the skill on the
    floor — delivered to `.ai-badger/skills/` and linked into no discovery directory (#261).

    Membership here is the directory, not the scope, because that is what "this stack owns it"
    actually means.
    """
    skills_dir = root / "features" / stack / "skills"
    if not skills_dir.is_dir():
        return []
    return sorted(
        d.name for d in skills_dir.iterdir()
        if d.is_dir() and (d / "SKILL.md").exists()
    )


def feature_items(index: Dict[str, Any], stack: str, feature: str) -> List[Dict[str, Any]]:
    """Return the index items for one stack's feature bucket (personas, skills, ...)."""
    return index.get("stacks", {}).get(stack, {}).get(feature, [])


def find_skill_in_stacks(index: Dict[str, Any], stacks: List[str],
                         skill_name: str) -> Tuple[Optional[Dict[str, Any]], str]:
    """Locate a skill by name across the given stacks. Returns (item, stack) or (None, '')."""
    for stack in stacks:
        hit = next((s for s in feature_items(index, stack, "skills")
                    if s["name"] == skill_name), None)
        if hit is not None:
            return hit, stack
    return None, ""


# ------------------------------------------------------------------------ catalog access
def read_index(root: Path) -> Dict[str, Any]:
    """Load the framework's generated index.json."""
    return load_json(root / "index.json")


DEFAULT_COMMON_STACKS = ["common"]


def resolve_stacks(config: Dict[str, Any]) -> List[str]:
    """Catalog stacks to read, always-included ones first, deduplicated in order.

    `config.commonStacks` names the always-included stack(s) — config.stacks may not
    contain them (config.schema.json forbids it), so a caller reading config.stacks
    alone never sees that catalog at all.
    """
    common = config.get("commonStacks", DEFAULT_COMMON_STACKS)
    if isinstance(common, str):
        common = [common]
    seen = set()
    return [s for s in list(common) + list(config.get("stacks", []))
            if not (s in seen or seen.add(s))]


def delivering_stacks(config: Dict[str, Any]) -> List[str]:
    """Every catalog stack this project draws from: configured stacks *and* configured agents.

    `config.agents` reads `features/<agent>/` directly — adjustments, templates, personas — with
    no entry in `config.stacks`, so an agent name is a catalog stack too. A caller that consults
    `resolve_stacks` alone judges every agent-delivered entry an orphan.
    """
    seen = set()
    return [s for s in resolve_stacks(config) + list(config.get("agents", []))
            if not (s in seen or seen.add(s))]


def discovery_stacks_for_agent(config: Dict[str, Any], agent: str) -> List[str]:
    """Catalog stacks whose delivered skills belong in `agent`'s discovery directory.

    Every stack the project draws from, minus the stacks that *are* the other agents: a
    dotnet or ai-raccoon skill is this agent's to discover, a copilot skill is not. Naming
    the qualifying stacks in a literal instead left every non-common stack's skills
    delivered to `.ai-badger/skills/` and linked nowhere (#261 one stack over).
    """
    others = {a for a in config.get("agents", []) if a != agent}
    return [s for s in delivering_stacks(config) if s not in others]


def applicable_feature_items(index: Dict[str, Any], config: Dict[str, Any],
                             feature: str) -> List[Tuple[str, Dict[str, Any]]]:
    """(stack, item) pairs the resolved stacks deliver for `feature`, minus `config.exclude`.

    The one stack-filtering rule — `scaffold_personas` and the Copilot agent delivery both
    read it, so the two hosts cannot disagree about which personas a project gets (#210).
    """
    declined = exclusions(config).get(feature, set())
    return [(stack, item)
            for stack in resolve_stacks(config)
            for item in feature_items(index, stack, feature)
            if item.get("name") not in declined]


def is_orphaned(entry: Dict[str, Any], delivering: List[str]) -> bool:
    """True when a manifest entry's stack is no longer one this project draws from.

    The one place that decides it, so drift and the re-scaffold cannot disagree about what a
    dropped stack leaves behind (#116). `delivering` is `delivering_stacks(config)` — passing
    `resolve_stacks(config)` instead silently condemns every agent-delivered entry.
    """
    return entry.get("stack") not in delivering


def iter_feature_dirs(root: Path) -> List[Tuple[str, str, Path]]:
    """Yield (stack, feature, dir) for every features/<stack>/<feature> directory present.

    Common skills live at features/common/skills/ and are discovered here like any other
    stack feature — no special-casing needed.
    """
    out: List[Tuple[str, str, Path]] = []
    features_root = root / "features"
    if not features_root.is_dir():
        return out
    for stack_dir in sorted(p for p in features_root.iterdir() if p.is_dir()):
        stack = stack_dir.name
        for feature in FEATURES:
            fdir = stack_dir / feature
            if fdir.is_dir():
                out.append((stack, feature, fdir))
    return out