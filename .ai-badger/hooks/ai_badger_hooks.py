"""Hermes plugin hooks for ai-badger framework integration.

Provides feature-parity with Claude Code hooks:
- on_session_start: drift notice (Tier 1, ADR-0001 decision 5)
- pre_llm_call: inject framework version context, usage hints, and MCP tool index recommendations
- pre_tool_call: memory-first gate — block text search until the session consulted memory_search
- post_tool_call: log tool usage, index hit/miss metrics, and learned-skill sync

Installation (0.80.0+): `welcome-ai-badger` ships these hooks as a Hermes
DIRECTORY plugin at ~/.hermes/plugins/ai-badger/ (plugin.yaml declaring the hooks,
__init__.py re-exporting register, and every sibling module beside this file);
Hermes discovers and loads it via register(ctx) — flat .py drops in ~/.hermes/plugins/
are invisible to Hermes' loader. The plugin is opt-in: `hermes plugins enable ai-badger`.

The plugin self-locates the framework root with the shared `_bootstrap_lib()` shim. In
~/.hermes/plugins/ai-badger/ there is no framework above these files, so the root recorded
in the plugin dir's own .ai-badger/manifest.json is what answers (ADR-0007 shape D).
"""

from __future__ import annotations

import importlib.util
import json
import time
import logging
import os
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

# debug_log sits beside this file in every deployment shape; it is a no-op unless the
# call-behaviorist skill has switched debug on.
sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    import debug_log  # pylint: disable=import-error
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

logger = logging.getLogger("ai_badger_hooks")


def _debug(component: str, event: str, **fields) -> None:
    """Record that a hook ran. Silent when debug is off or the logger is unavailable."""
    if debug_log is not None:
        debug_log.log_event(component, event, **fields)


# Sibling module names already reported broken this process — logged once, not once per call.
_broken_siblings: set = set()


def _load_sibling_module(module_name: str, filename: str, label: str) -> Optional[Any]:
    """Import a sibling module beside this file, lazily and cached; None when absent or broken.

    Absent (no file) fails open in silence — an older scaffold legitimately lacks it. Broken
    (raises on import) logs "<label> disabled" once per process via logger.warning and _debug,
    then keeps returning None without re-attempting the import.
    """
    cached = sys.modules.get(module_name)
    if cached is not None:
        return cached
    if module_name in _broken_siblings:
        return None
    path = Path(__file__).resolve().parent / filename
    if not path.is_file():
        return None
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        return None
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    try:
        spec.loader.exec_module(module)
    except Exception:  # pylint: disable=broad-exception-caught
        sys.modules.pop(module_name, None)
        _broken_siblings.add(module_name)
        logger.warning("%s disabled: %s could not be loaded from %s", label, module_name, path,
                       exc_info=True)
        _debug("ai_badger_hooks/sibling_load", "broken", module=module_name, label=label)
        return None
    return module


# ---------------------------------------------------------------------------
# Framework root discovery — the shim every ai-badger entry point carries
# ---------------------------------------------------------------------------

def _bootstrap_lib() -> Path:
    """Put the framework's engine/ and tooling/ on sys.path and return its root.

    One predicate, shared with badger_lib.is_framework_root: schemas/ + features/ +
    engine/badger_lib.py. Ordered inputs: --root, an ancestor walk, $AI_BADGER, the root
    recorded in a .ai-badger/manifest.json above this file, then ~/.ai-badger/framework
    (ADR-0009). Duplicated verbatim in every entry point because locating badger_lib is
    what it is for.
    """
    def is_root(path):
        return ((path / "schemas").is_dir() and (path / "features").is_dir()
                and (path / "engine" / "badger_lib.py").is_file())

    def argv_root():
        # sys.argv is ours only when this file is the program being run; these modules are
        # also imported into hosts whose own --root means something else entirely.
        try:
            if not sys.argv or Path(sys.argv[0]).resolve() != Path(__file__).resolve():
                return None
        except (OSError, ValueError):
            return None
        argv = sys.argv[1:]
        for i, arg in enumerate(argv):
            if arg == "--root" and i + 1 < len(argv):
                return argv[i + 1]
            if arg.startswith("--root="):
                return arg.split("=", 1)[1]
        return None

    def checked(value, source):
        root = Path(value).expanduser()
        if not is_root(root):
            raise RuntimeError(
                f"{source} is {root}, which is not an ai-badger framework root "
                f"(no schemas/ + features/ + engine/badger_lib.py)"
            )
        return root

    def manifests(start):
        # Above this file only. A working directory belongs to whatever repo the user
        # opened, and no repo may steer the sys.path of a hook that runs on session start.
        for anc in [start, *start.parents]:
            manifest = (anc / "manifest.json" if anc.name == ".ai-badger"
                        else anc / ".ai-badger" / "manifest.json")
            if not manifest.is_file():
                continue
            try:
                data = json.loads(manifest.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if isinstance(data, dict):
                yield manifest, data

    def recorded(start):
        for manifest, data in manifests(start):
            value = data.get("frameworkRoot")
            if not value:
                continue
            candidate = Path(value).expanduser()
            if not candidate.is_absolute():
                candidate = manifest.parent.parent / candidate
            if is_root(candidate):
                return candidate.resolve()
        return None

    def warn_on_cache_skew(root, start):
        # The cache is last in the order and never updated in place, so its engine can be
        # many releases behind the caller. Say so; never break a session over it.
        if root.resolve() != cache.resolve():
            return
        try:
            have = (cache / "VERSION").read_text(encoding="utf-8").strip()
        except OSError:
            return
        want = next((d.get("frameworkVersion") for _, d in manifests(start)
                     if d.get("frameworkVersion")), None)
        if have and want and have != want:
            print(f"ai-badger: {cache} is version {have}, but this project was scaffolded "
                  f"by {want}. The cache is never updated in place — remove it, or pass "
                  f"--root <framework checkout>.", file=sys.stderr)

    here = Path(__file__).resolve()
    cache = Path.home() / ".ai-badger" / "framework"
    value = argv_root()
    if value:
        root = checked(value, "--root")
    else:
        root = next((anc for anc in [here, *here.parents] if is_root(anc)), None)
        if root is None and os.environ.get("AI_BADGER"):
            root = checked(os.environ["AI_BADGER"], "$AI_BADGER")
        root = root or recorded(here) or (cache if is_root(cache) else None)
    if root is None:
        raise RuntimeError(
            f"could not locate the ai-badger framework: none above {here.parent}, no "
            f"$AI_BADGER, no frameworkRoot in a .ai-badger/manifest.json above it, and no "
            f"cache at {cache} — pass --root <framework> or clone "
            f"https://github.com/Arasz/ai-badger"
        )
    warn_on_cache_skew(root, here)
    sys.path.insert(0, str(root / "tooling"))
    sys.path.insert(0, str(root / "engine"))
    return root.resolve()


try:
    FRAMEWORK_ROOT: Optional[Path] = _bootstrap_lib()
except RuntimeError as _bootstrap_failure:  # never break a session; never hide the reason
    print(f"ai-badger: {_bootstrap_failure}", file=sys.stderr)
    FRAMEWORK_ROOT = None


def _copy_skew_refusal() -> Optional[str]:
    """Why these copies must not run, or None. Warns in passing when the skew is not material.

    Shape D only: any other deployment has no installer record beside this file, so
    `badger_lib.copy_skew` returns OK and this costs one missing-file stat.
    """
    if FRAMEWORK_ROOT is None:
        return None
    try:
        import badger_lib as bl  # pylint: disable=import-outside-toplevel  # needs the bootstrap
        verdict, message = bl.copy_skew(Path(__file__).resolve().parent, FRAMEWORK_ROOT)
    except (AttributeError, ImportError, OSError, ValueError):
        return None  # a staleness check never decides whether the plugin loads
    if verdict == bl.COPY_SKEW_REFUSE:
        return message
    if verdict == bl.COPY_SKEW_WARN:
        print(f"ai-badger: {message}", file=sys.stderr)
    return None


COPY_SKEW_REFUSAL: Optional[str] = _copy_skew_refusal()


# ---------------------------------------------------------------------------
# Drift notice — equivalent to Claude's SessionStart drift_notice_hook.py
# ---------------------------------------------------------------------------

def _read_framework_version() -> Optional[str]:
    """Read the framework's VERSION file, or None on any error."""
    if FRAMEWORK_ROOT is None:
        return None
    try:
        return (FRAMEWORK_ROOT / "VERSION").read_text(encoding="utf-8").strip()
    except OSError:
        return None


def _project_cwd(cwd: str = "") -> str:
    """Resolve the project directory for a hook callback.

    Hermes passes no `cwd` to any plugin hook, so the process working directory is the
    only signal available. See issue #76.
    """
    return cwd or os.getcwd()


def _read_scaffold_version(cwd: Optional[str]) -> Optional[str]:
    """Read the project's manifest.json frameworkVersion, or None."""
    if not cwd:
        return None
    manifest = Path(cwd) / ".ai-badger" / "manifest.json"
    if not manifest.exists():
        return None
    try:
        data = json.loads(manifest.read_text(encoding="utf-8"))
        if not isinstance(data, dict):
            return None
        return data.get("frameworkVersion")
    except (OSError, ValueError):
        return None


def _parse_major_minor(version: str) -> Optional[Tuple[int, int]]:
    """(major, minor) from a `x.y[.z]` string, or None when it does not parse."""
    parts = version.split(".")
    if len(parts) < 2:
        return None
    try:
        return int(parts[0]), int(parts[1])
    except ValueError:
        return None


# Duplicated from features/common/skills/task/scripts/drift_notice.py: this Hermes hook lives in
# a directory that cannot import that Claude-side module cleanly. test_both_hosts_agree_on_
# versions_diverge in tests/test_hook_cwd_resolution.py pins both spellings, the same convention
# tests/conftest.py's SPAWN_LOG_ENV/REAL_WRITE_LOG_ENV comments describe.
def versions_diverge(scaffolded: str, running: str) -> bool:
    """True when a re-scaffold could change something a consumer would see.

    Compares (major, minor) only — a patch-only difference is stamp noise, the same
    tolerance `gates/scaffold_freshness_guard.py`'s STAMP_KEYS already grant (B10). An
    unparseable version on either side falls back to plain string inequality.
    """
    parsed_scaffolded = _parse_major_minor(scaffolded)
    parsed_running = _parse_major_minor(running)
    if parsed_scaffolded is None or parsed_running is None:
        return scaffolded != running
    return parsed_scaffolded != parsed_running


# Hints already injected in this session; cleared at session start.
_session_hints_shown: set = set()


def reset_session_hints() -> None:
    """Forget which one-per-session hints have been shown."""
    _session_hints_shown.clear()


# ---------------------------------------------------------------------------
# Memory-first gate — Hermes has no shell-hook session_id, so state is
# per-process keyed by project cwd; a gateway handling several sessions for one
# project shares the consulted flag until the next on_session_start reset.
# ---------------------------------------------------------------------------

MEMORY_FIRST_GATE_MODULE_NAME = "memory_first_gate"

# Projects (resolved cwd) that already ran memory_search this session.
_memory_consulted: set = set()
# Denials per project; after MAX_DENIALS the gate passes through so an agent
# cannot stall on a bank that simply has no hit for its query.
_gate_denials: dict = {}


def reset_gate_state() -> None:
    """Forget the per-session memory-first gate state (called at session start)."""
    _memory_consulted.clear()
    _gate_denials.clear()


def _load_memory_first_gate() -> Optional[Any]:
    """Import the sibling memory_first_gate module lazily; None when absent or broken.

    An older scaffold without the module gets no gate at all — fail open, never
    block a session because a sibling module is missing.
    """
    return _load_sibling_module(MEMORY_FIRST_GATE_MODULE_NAME, "memory_first_gate.py",
                                "memory-first gate")


def _gate_project() -> str:
    """The project the gate keys state on; plugin callbacks carry no cwd."""
    return _project_cwd("")


def _is_memory_search(tool_name: Any) -> bool:
    """True for any naming spelling of the memory_search tool; never matches other tools."""
    if not isinstance(tool_name, str):
        return False
    name = tool_name
    if name.startswith("mcp__"):
        name = name[len("mcp__"):].split("__", 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    return name == "memory_search"


def _maybe_record_memory_consult(tool_name: str, args: Dict[str, Any], cwd: str,
                                 session_id: Optional[str] = None) -> None:
    """After a memory_search call, unlock the gate for this project's session."""
    if not _is_memory_search(tool_name):
        return
    project = _project_cwd(cwd)
    _memory_consulted.add(project)
    gate = _load_memory_first_gate()
    if gate is not None:
        gate.record_search(session_id)


def pre_tool_call_memory_gate(tool_name: str = "", args: Optional[Dict[str, Any]] = None,
                              task_id: Optional[str] = None, **_kwargs: Any) -> Optional[Dict[str, str]]:
    """Block text-search tools until the session's first memory_search call.

    Returns a block decision naming memory_search, or None to allow. Never blocks
    once consulted, after MAX_DENIALS, or when the gate module is missing.
    """
    gate = _load_memory_first_gate()
    if gate is None:
        return None
    project = _gate_project()
    if project in _memory_consulted:
        return None
    tool_name = tool_name or _kwargs.get("function_name") or ""
    if not gate.is_text_search(tool_name, args or {}):
        return None
    denials = _gate_denials.get(project, 0)
    if denials >= gate.MAX_DENIALS:
        return None
    _gate_denials[project] = denials + 1
    return gate.build_decision("hermes", tool_name, args or {}, None, cwd=project)


def on_session_start_drift_notice(cwd: str = "", **_kwargs: Any) -> None:
    """Check for framework version drift (see `versions_diverge`) on every session start.

    Silent on match, on a patch-only bump, on an unscaffolded project, and on any read
    error. A hook that breaks session start or nags unconditionally defeats its purpose.
    """
    reset_session_hints()
    reset_gate_state()
    project = _project_cwd(cwd)
    _debug("ai_badger_hooks/session_start", "start", project=project)
    scaffold_ver = _read_scaffold_version(project)
    fw_version = _read_framework_version()
    if not scaffold_ver or not fw_version or not versions_diverge(scaffold_ver, fw_version):
        _debug("ai_badger_hooks/session_start", "skip", project=project,
               scaffold_version=scaffold_ver, framework_version=fw_version)
        return
    _debug("ai_badger_hooks/session_start", "drift", project=project,
           scaffold_version=scaffold_ver, framework_version=fw_version)
    logger.info(
        "ai-badger drift: scaffolded with %s, framework is %s. "
        "Run den-refresh to update.",
        scaffold_ver, fw_version,
    )


# ---------------------------------------------------------------------------
# MCP Tool Index integration
# ---------------------------------------------------------------------------

MCP_MATCHER_MODULE_NAME = "ai_badger_mcp_matcher"


def _load_mcp_matcher() -> Optional[Any]:
    """Import the sibling BM25 matcher lazily; None when an older scaffold lacks it, or it's broken.

    The tokenizer, scoring, gate and document construction all live in
    features/common/retrieval/mcp_matcher.py (docs/adr/0012) — this hook only calls it.
    """
    return _load_sibling_module(MCP_MATCHER_MODULE_NAME, "mcp_matcher.py", "MCP tool matcher")


def _load_mcp_index(cwd: Optional[str]) -> Optional[dict[str, Any]]:
    """Load .ai-badger/mcp-tools.json from the project, or None.

    JSON-only by design (docs/adr/0012 §4, issue #145): a dual-format reader here would
    keep `import yaml` on this hook's path permanently, which is the whole point of
    removing it. A project still on the legacy `mcp-tools.yaml` gets no MCP recommendations
    — the same silence a missing index already produces — until `mcp-index migrate` (or
    any write command) upgrades it; `mcp_index.py`'s reader stays dual-format for exactly
    that migration.
    """
    if not cwd:
        return None
    index_path = Path(cwd) / ".ai-badger" / "mcp-tools.json"
    if not index_path.exists():
        return None
    try:
        return json.loads(index_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def _has_legacy_unmigrated_index(cwd: Optional[str]) -> bool:
    """True when the project has a not-yet-migrated legacy mcp-tools.yaml.

    `_load_mcp_index` returns None both for this and for a genuinely missing index — this
    is what lets a caller tell the two apart before logging, so `absent` in the retrieval
    telemetry keeps meaning "no index" rather than conflating it with "not migrated yet"
    (issue #145 review finding).
    """
    if not cwd:
        return False
    aib = Path(cwd) / ".ai-badger"
    return (aib / "mcp-tools.yaml").exists() and not (aib / "mcp-tools.json").exists()


def _find_relevant_tools(
    query: str, index: dict[str, Any], top_n: int = 3
) -> list[tuple[str, float]]:
    """Rank tools in the index by relevance to the query via the BM25 matcher.

    Returns list of (full_tool_name, score) sorted by descending score, gated by
    coverage rather than an all-or-nothing tag lookup (docs/adr/0012). `[]` when
    the matcher module isn't available (older scaffold) or nothing clears the gate.
    """
    matcher = _load_mcp_matcher()
    if matcher is None:
        return []
    return [(r.tool, r.score) for r in matcher.find_relevant_tools(query, index, top_n=top_n)]


# The component retrieval events log under — reusing the file's existing component name,
# not inventing one behaviorist.py's orphaned-wiring check would flag as unexpected.
_MCP_RETRIEVAL_COMPONENT = "ai_badger_hooks/mcp_retrieval"

# Wire keys for retrieval telemetry, registered in debug_log.KEY_NAMES; the words live there.
_KEY_QUERY = "q"
_KEY_TERMS = "g"
_KEY_CANDIDATES = "d"
_KEY_TOP = "o"
_KEY_RETURNED = "r"
_KEY_THRESHOLD = "h"
_KEY_TOOL = "l"


def _score_all_tools(query: str, index: dict[str, Any]) -> list[Any]:
    """Every non-removed tool in the index, BM25-ranked against `query`.

    Unfiltered by the coverage gate: gate telemetry needs the near-misses, not just the
    winners. Returns `bm25.ScoredDoc` (`.doc_id`, `.score`, `.coverage`, `.matched_terms`);
    `[]` when the matcher is unavailable, the index has no tools, or the query tokenizes
    to nothing.
    """
    matcher = _load_mcp_matcher()
    if matcher is None:
        return []
    corpus = matcher.build_corpus(index)
    if corpus is None:
        return []
    query_terms = matcher.tokenize(query)
    if not query_terms:
        return []
    return corpus.rank(query_terms, coverage_cap=matcher.COVERAGE_TERM_CAP)


def _index_tool_count(index: dict[str, Any]) -> int:
    """Count of non-removed tools across every source in the index."""
    return sum(
        1
        for server in index.get("sources", [])
        for tool in server.get("tools", {}).values()
        if tool.get("status") != "removed"
    )


def _format_top_candidates(scored: list[Any], limit: int = 3) -> str:
    """`name:score:coverage` for the top candidates, or `name:score` if the triples don't
    fit debug_log's 200-char field clip.

    Capped at 3: enough triples to be useful without truncating a candidate's name
    mid-string when the coverage field has to be dropped to stay under the clip.
    """
    top = scored[:limit]
    with_coverage = ",".join(f"{r.doc_id}:{r.score:.2f}:{r.coverage:.2f}" for r in top)
    if len(with_coverage) <= 200:
        return with_coverage
    return ",".join(f"{r.doc_id}:{r.score:.2f}" for r in top)


def _record_retrieval(project, query: str, index: dict, ranked: list) -> None:
    """Record what the BM25 retrieval did. Costs nothing when debug is off.

    `no_terms` is not `gate`: when the tokenizer yields no usable terms, no candidate is
    ever scored against the coverage threshold. Reporting that as a threshold miss would
    misattribute the very failure this telemetry exists to count, and it carries no
    threshold field for the same reason — none was applied.
    """
    if debug_log is None or not debug_log.enabled_for(project):
        return
    matcher = _load_mcp_matcher()
    query_terms = matcher.tokenize(query) if matcher is not None else []
    scored = _score_all_tools(query, index)
    common = {
        _KEY_QUERY: query,
        _KEY_CANDIDATES: _index_tool_count(index),
        _KEY_TOP: _format_top_candidates(scored),
    }
    if not query_terms:
        _debug(_MCP_RETRIEVAL_COMPONENT, "no_terms", project=project,
               **{**common, _KEY_RETURNED: ""})
        return
    threshold = matcher.DEFAULT_COVERAGE_THRESHOLD if matcher is not None else None
    scoring = {**common, _KEY_TERMS: ",".join(sorted(set(query_terms))),
               _KEY_THRESHOLD: threshold}
    if ranked:
        _debug(_MCP_RETRIEVAL_COMPONENT, "hit", project=project,
               **{**scoring, _KEY_RETURNED: ", ".join(name for name, _ in ranked)})
    else:
        _debug(_MCP_RETRIEVAL_COMPONENT, "gate", project=project,
               **{**scoring, _KEY_RETURNED: ""})


def _record_tool_index_check(project, tool_name: str, index: dict[str, Any]) -> None:
    """Record whether a called MCP tool was one the index knew — server absence included.

    `server_unindexed` is not `unknown` (issue #170): an unknown tool on an indexed server
    wants `mcp-index update`, a server no source names wants indexing at all. A server
    carrying `status: empty`/`unknown` is present, so it answers `unknown`. Unqualified
    tool names are built-ins, not MCP tools, and are not recorded.
    """
    sname, _, tname = tool_name.partition(":")
    if not tname:
        return
    for server in index.get("sources", []):
        if server.get("name") != sname:
            continue
        event = "known" if tname in server.get("tools", {}) else "unknown"
        _debug(_MCP_RETRIEVAL_COMPONENT, event, project=project, **{_KEY_TOOL: tool_name})
        return
    _debug(_MCP_RETRIEVAL_COMPONENT, "server_unindexed", project=project,
           **{_KEY_TOOL: tool_name})


# ---------------------------------------------------------------------------
# Context enrichment — equivalent to Claude's UserPromptSubmit hook
# ---------------------------------------------------------------------------

def pre_llm_inject_context(
    cwd: str = "", message: str = "", user_message: str = "", **_kwargs: Any
) -> Optional[Dict[str, str]]:
    """Inject ai-badger framework context into every LLM turn.

    Returns a context dict that Hermes prepends to the user message,
    or None to leave the prompt unchanged. This fires once per turn,
    before the tool-calling loop.

    What we inject:
    - Framework version info (so the agent knows which ai-badger features are available)
    - Drift notice if the project is behind
    - Hermes-specific usage hints (/usage, hermes insights, session_search)
    - MCP tool index recommendations (when .ai-badger/mcp-tools.json exists)
    - A pending commit-reminder nudge stashed by post_tool_observer, surfaced once
    """
    parts: list[str] = []
    project = _project_cwd(cwd)
    prompt = message or user_message

    pending_reminder = _pop_pending_reminder(project)
    if pending_reminder:
        parts.append(pending_reminder)

    pending_grade = _pop_pending_grade(project)
    if pending_grade:
        cid = pending_grade.get("correlationId", "")
        parts.append(
            f"[ai-badger] Rate that memory_search's usefulness 1-5 (5=best, or skip): "
            f"python3 {Path(__file__).resolve().parent / 'memory_grade.py'} grade {cid} <1-5> [note]"
        )

    # Framework version — see `versions_diverge`; a patch-only bump is silent (B10).
    fw_version = _read_framework_version()
    if fw_version:
        scaffold_ver = _read_scaffold_version(project)
        if scaffold_ver and versions_diverge(scaffold_ver, fw_version):
            parts.append(
                f"[ai-badger] Scaffolded with {scaffold_ver}, "
                f"framework is {fw_version}. Run den-refresh to update."
            )

    # Usage hints — once per session. Repeated every turn, they became wallpaper: the
    # 0.18.0 changelog records an unconditional line as why a broken hook went unnoticed.
    if not _session_hints_shown:
        _session_hints_shown.add("usage")
        parts.append(
            "[Hermes] Use /usage for token consumption and model info. "
            "Use hermes insights --days 7 for weekly analytics. "
            "Use session_search to recall past decisions."
        )

    # MCP tool index recommendations
    if prompt:
        index = _load_mcp_index(project)
        if index is None:
            event = "legacy" if _has_legacy_unmigrated_index(project) else "absent"
            _debug(_MCP_RETRIEVAL_COMPONENT, event, project=project,
                   **{_KEY_QUERY: prompt})
        else:
            ranked = _find_relevant_tools(prompt, index, top_n=3)
            if ranked:
                tools_str = ", ".join(
                    f"{name} ({', '.join(tags_for_display(name, index))})"
                    for name, _ in ranked[:3]
                )
                # Keep under 300 chars to avoid prompt bloat
                hint = f"[ai-badger] Relevant MCP tools: {tools_str}"
                if len(hint) > 300:
                    # Both branches above are already top-3; this fallback only strips
                    # the per-tool tags parenthetical to fit the budget.
                    tools_str_short = ", ".join(
                        f"{name}" for name, _ in ranked[:3]
                    )
                    hint = f"[ai-badger] Relevant MCP tools: {tools_str_short}"
                parts.append(hint)
            _record_retrieval(project, prompt, index, ranked)

    if not parts:
        return None
    return {"context": "\n".join(parts)}


def tags_for_display(tool_name: str, index: dict[str, Any]) -> list[str]:
    """Helper to look up tags for a tool in the index. Used in pre_llm_inject_context."""
    if ":" in tool_name:
        sname, tname = tool_name.split(":", 1)
        for server in index.get("sources", []):
            if server["name"] == sname:
                tool = server.get("tools", {}).get(tname, {})
                return tool.get("tags", [])
    return []


# ---------------------------------------------------------------------------
# Learned-skill sync — wiring only; logic lives in learned_skills_sync.py
# ---------------------------------------------------------------------------

SKILL_MANAGE_TOOL = "skill_manage"
SYNC_MODULE_NAME = "ai_badger_learned_skills_sync"


def hermes_skills_root() -> Path:
    """Resolve the Hermes skills root: HERMES_HOME wins, else the platform default."""
    override = os.environ.get("HERMES_HOME", "").strip()
    base = Path(override).expanduser() if override else Path.home() / ".hermes"
    return base / "skills"


def _load_learned_skills_sync() -> Optional[Any]:
    """Import the sibling sync module lazily; None when absent, or it's broken."""
    return _load_sibling_module(SYNC_MODULE_NAME, "learned_skills_sync.py", "learned-skill sync")


def _sync_learned_skill(args: Dict[str, Any], status: str, cwd: str) -> None:
    """Hand one skill_manage call to the sync.

    Hermes' post_tool_call payload carries no cwd, so the process cwd is the only
    project signal available; the sync itself no-ops unless it is a scaffolded project.
    """
    sync = _load_learned_skills_sync()
    if sync is None:
        return
    outcome = sync.on_skill_manage(
        args, status, cwd or os.getcwd(),
        skills_root=hermes_skills_root(),
        now=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        tool_name=SKILL_MANAGE_TOOL,
    )
    if outcome:
        logger.debug("learned-skill sync: %s", outcome)


# ---------------------------------------------------------------------------
# Commit reminder — Hermes has no PostToolUse return channel into the model's
# context, so the check runs here and stashes a pending nudge that
# pre_llm_inject_context surfaces on the very next turn (docs: commit-reminder skill).
# ---------------------------------------------------------------------------

PENDING_GRADE_FILE = Path.home() / ".ai-badger" / "memory-grade" / "pending.json"


def _load_pending_grades() -> Dict[str, Dict[str, str]]:
    """Load the pending-grade file; `{}` on missing/read/malformed."""
    try:
        raw = PENDING_GRADE_FILE.read_text(encoding="utf-8")
    except OSError:
        return {}
    try:
        data = json.loads(raw)
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def _save_pending_grades(pending: Dict[str, Dict[str, str]]) -> None:
    """Persist the pending-grade file, creating parent directories as needed."""
    PENDING_GRADE_FILE.parent.mkdir(parents=True, exist_ok=True)
    PENDING_GRADE_FILE.write_text(json.dumps(pending), encoding="utf-8")


def _set_pending_grade(project: str, correlation_id: str, query: str) -> None:
    """Stash a grade ask for `project` keyed by its resolved absolute path."""
    pending = _load_pending_grades()
    pending[str(Path(project).resolve())] = {
        "correlationId": correlation_id,
        "query": query[:200],
    }
    _save_pending_grades(pending)


def _pop_pending_grade(project: str) -> Optional[Dict[str, str]]:
    """Return and clear the pending grade for `project`, or None."""
    pending = _load_pending_grades()
    key = str(Path(project).resolve())
    entry = pending.pop(key, None)
    if entry is not None:
        _save_pending_grades(pending)
    return entry


def _is_read_file(tool_name: Any) -> bool:
    """True for file-reading tool names across agents (Hermes/Claude/Copilot)."""
    if not isinstance(tool_name, str):
        return False
    name = tool_name
    if name.startswith("mcp__"):
        name = name[len("mcp__"):].split("__", 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    return name in ("read_file", "Read", "ReadFile", "readfile")


# In-memory stash of recent search results for follow-through correlation.
# Keyed by project path; entries expire after FOLLOW_THROUGH_WINDOW seconds.
_RECENT_SEARCHES: Dict[str, list] = {}
FOLLOW_THROUGH_WINDOW = 60  # seconds


def _stash_search_sources(tool_name: str, result: str, cwd: str) -> None:
    """After memory_search, stash {correlationId, sourceFiles, timestamp} for follow-through."""
    if not _is_memory_search(tool_name):
        return
    try:
        data = json.loads(result) if isinstance(result, str) else result
    except (ValueError, TypeError):
        return
    if not isinstance(data, dict):
        return
    # Extract correlationId from envelope meta
    meta = data.get("meta") or data.get("Meta") or {}
    corr_id = meta.get("correlationId") or meta.get("CorrelationId") or ""
    if not corr_id:
        return
    # Extract sourceFile paths from search results
    search_results = data.get("results") or data.get("data") or []
    source_files = []
    for r in search_results:
        sf = r.get("sourceFile") or r.get("SourceFile")
        if sf and sf not in source_files:
            source_files.append(sf)
    if not source_files:
        return
    project = _project_cwd(cwd)
    now = time.time()
    entry = {"correlationId": corr_id, "sourceFiles": source_files, "ts": now}
    _RECENT_SEARCHES.setdefault(project, []).append(entry)
    # Prune old entries
    _RECENT_SEARCHES[project] = [
        e for e in _RECENT_SEARCHES[project] if now - e["ts"] < FOLLOW_THROUGH_WINDOW * 2
    ]
    _debug("ai_badger_hooks/follow_through", "stashed",
           project=project, corr_id=corr_id, files=len(source_files))


def _maybe_record_follow_through(tool_name: str, result: str, cwd: str) -> None:
    """After read_file, check if the path matches a recent search result's sourceFile."""
    if not _is_read_file(tool_name):
        return
    try:
        data = json.loads(result) if isinstance(result, str) else result
    except (ValueError, TypeError):
        data = {}
    if not isinstance(data, dict):
        data = {}
    # Extract file path from args or result
    file_path = data.get("path") or data.get("filePath") or data.get("file_path") or ""
    if not file_path:
        return
    file_path = str(Path(file_path).resolve())
    project = _project_cwd(cwd)
    now = time.time()
    entries = _RECENT_SEARCHES.get(project, [])
    for entry in entries:
        if now - entry["ts"] > FOLLOW_THROUGH_WINDOW:
            continue
        for sf in entry["sourceFiles"]:
            try:
                sf_resolved = str(Path(sf).resolve())
            except (ValueError, OSError):
                continue
            if file_path == sf_resolved or file_path.startswith(sf_resolved + "/"):
                # Match — write follow-through to search_quality table
                _record_follow_through_sql(entry["correlationId"], sf)
                _debug("ai_badger_hooks/follow_through", "recorded",
                       project=project, corr_id=entry["correlationId"], file=sf)
                return  # first match wins


def _record_follow_through_sql(correlation_id: str, file_path: str) -> None:
    """Write follow-through directly to the search_quality table via SQLite."""
    db_path = Path.home() / ".ai-raccoon" / "memory.db"
    if not db_path.exists():
        return
    try:
        conn = sqlite3.connect(str(db_path))
        try:
            row = conn.execute(
                "SELECT follow_through_files FROM search_quality WHERE correlation_id = ?",
                (correlation_id,)
            ).fetchone()
            if row is None:
                return
            existing = row[0] or "[]"
            try:
                files = json.loads(existing)
            except (ValueError, TypeError):
                files = []
            if not isinstance(files, list):
                files = []
            if file_path not in files:
                files.append(file_path)
            conn.execute(
                "UPDATE search_quality SET follow_through_count = ?, follow_through_files = ? WHERE correlation_id = ?",
                (len(files), json.dumps(files), correlation_id)
            )
            conn.commit()
        finally:
            conn.close()
    except Exception:  # pylint: disable=broad-exception-caught
        pass


def _maybe_stash_grade_ask(tool_name: str, result: str, cwd: str) -> None:
    """After a memory_search call, stash a grade ask with the correlationId from the response."""
    if not _is_memory_search(tool_name):
        return
    if not result:
        return
    try:
        data = json.loads(result) if isinstance(result, str) else result
    except (ValueError, TypeError):
        return
    if not isinstance(data, dict):
        return
    meta = data.get("meta") or data.get("Meta") or {}
    correlation_id = meta.get("correlationId") or meta.get("CorrelationId")
    if not correlation_id:
        return
    project = _project_cwd(cwd)
    _set_pending_grade(project, correlation_id, "")
    _debug("ai_badger_hooks/memory_grade", "stashed", project=project,
           correlation_id=correlation_id)


def _maybe_remind_commit(tool_name: str, cwd: str) -> None:
    """After an edit-shaped tool call, ratchet-check the uncommitted count and stash a nudge.

    Guard: an unavailable module or a non-edit tool name skips before any git call.
    """
    commit_reminder = _load_commit_reminder()
    if commit_reminder is None or not commit_reminder.is_edit_tool(tool_name):
        _debug("ai_badger_hooks/commit_reminder", "skip", tool_name=tool_name)
        return

    project = _project_cwd(cwd)
    files = commit_reminder.uncommitted_files(project)
    count = len(files)
    threshold = _commit_reminder_threshold()
    _debug("ai_badger_hooks/commit_reminder", "checked", project=project,
           count=count, threshold=threshold)

    # Same entry the Claude hook maintains: writing a bare marker here would drop the
    # unanswered count and silently clear an escalation raised on the other side.
    fires, at_risk, entry = commit_reminder.advance(
        commit_reminder.get_entry(project), count, threshold,
        _commit_escalate_after(), now=_now_iso(),
    )
    commit_reminder.set_entry(project, entry)
    if not fires:
        return

    impact_estimator = _load_impact_estimator()
    if impact_estimator is not None:
        use_graph = os.environ.get(COMMIT_REMINDER_IMPACT_ENV) == "graph"
        impact = impact_estimator.estimate_impact(files, project, use_graph=use_graph)
    else:
        impact = f"{count} file(s) changed"
    message = commit_reminder.build_message(count, impact, entry["fires"], at_risk)
    _set_pending_reminder(project, message)
    _debug("ai_badger_hooks/commit_reminder", "fire", project=project,
           count=count, threshold=threshold)


# ---------------------------------------------------------------------------
# Tool call observer — equivalent to Claude's PostToolUse hook
# ---------------------------------------------------------------------------

def post_tool_observer(tool_name: str = "", result: str = "",
                       duration_ms: int = 0, cwd: str = "", **kwargs: Any) -> None:
    """Observe tool calls for debugging and metrics.

    Fires after every tool execution. Logs at DEBUG level so it doesn't flood
    the console. Enable by setting LOG_LEVEL=DEBUG on the ai_badger_hooks logger.

    Hermes' plugin emitter sends the post_tool_call payload as
    ``function_name``/``function_args``/``session_id`` (model_tools
    _emit_post_tool_call_hook), not the shell-hook ``tool_name``/``args``/``cwd``
    spelling — normalize both so the observer works under either transport. No
    payload carries ``cwd``; fall back to the session process cwd, which is what
    pre_llm_inject_context resolves on the pop side.
    """
    tool_name = tool_name or kwargs.get("function_name") or ""
    args = kwargs.get("args") or kwargs.get("function_args") or {}
    if isinstance(args, str):  # a transport may serialize function_args as JSON text
        try:
            args = json.loads(args)
        except ValueError:
            args = {}
    cwd = cwd or kwargs.get("cwd") or os.getcwd()
    session_id = kwargs.get("session_id")

    logger.debug(
        "tool=%s duration_ms=%d result_len=%d",
        tool_name, duration_ms, len(result) if result else 0,
    )

    if tool_name == SKILL_MANAGE_TOOL:
        try:
            _sync_learned_skill(args, kwargs.get("status", "ok"), cwd)
        except Exception:  # pylint: disable=broad-exception-caught
            logger.warning("learned-skill sync failed", exc_info=True)

    try:
        _maybe_remind_commit(tool_name, cwd)
    except Exception:  # pylint: disable=broad-exception-caught
        logger.warning("commit reminder check failed", exc_info=True)

    try:
        _maybe_record_memory_consult(tool_name, args, cwd, session_id)
    except Exception:  # pylint: disable=broad-exception-caught
        logger.warning("memory-first gate record failed", exc_info=True)

    try:
        _maybe_stash_grade_ask(tool_name, result, cwd)
    except Exception:  # pylint: disable=broad-exception-caught
        logger.warning("memory-grade stash failed", exc_info=True)

    try:
        _stash_search_sources(tool_name, result, cwd)
    except Exception:  # pylint: disable=broad-exception-caught
        logger.warning("follow-through stash failed", exc_info=True)

    try:
        _maybe_record_follow_through(tool_name, result, cwd)
    except Exception:  # pylint: disable=broad-exception-caught
        logger.warning("follow-through record failed", exc_info=True)

    # Log index hit/miss metrics if the index is available
    if tool_name:
        project = _project_cwd(cwd)
        index = _load_mcp_index(project)
        if index:
            _record_tool_index_check(project, tool_name, index)


# ---------------------------------------------------------------------------
# Plugin entry point — called by Hermes plugin loader
# ---------------------------------------------------------------------------

def register(ctx: Any) -> None:
    """Register all ai-badger hooks with the Hermes plugin system.

    The `ctx` object provides ctx.register_hook(name, callback).
    All callbacks accept **kwargs for forward compatibility — new
    parameters added in future Hermes versions won't break this plugin.

    Stale copies register nothing: the plugin still loads, so the session is unaffected, and
    the agent runs with ai-badger's hooks absent rather than wrong.
    """
    if COPY_SKEW_REFUSAL:
        print(f"ai-badger: hooks not registered — {COPY_SKEW_REFUSAL}", file=sys.stderr)
        logger.warning("ai-badger hooks not registered: %s", COPY_SKEW_REFUSAL)
        return
    ctx.register_hook("on_session_start", on_session_start_drift_notice)
    ctx.register_hook("pre_llm_call", pre_llm_inject_context)
    ctx.register_hook("pre_tool_call", pre_tool_call_memory_gate)
    ctx.register_hook("post_tool_call", post_tool_observer)
    logger.info("ai-badger hooks registered: on_session_start, pre_llm_call, "
                "pre_tool_call, post_tool_call")