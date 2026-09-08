"""P1 port gates for the post-fusion diversifier PoC port (plan §P1-1b, AC1.3 / AC1.4).

Branch-only gates (the 4 C# files are branch-local forever — merge policy in the plan).
All three are env-gated on `AI_RACCOON_EVAL_COPY` (a bank copy made by
`scripts/retrieval_tuning/make_memory_copy.py`) plus `AI_RACCOON_EVAL_DLL` (a built
AiRaccoon.dll) and skip without them; with `AI_RACCOON_EVAL_COPY` set on main they would
fail (no port), by design.

- test_parity_vs_fixture_default_off — the 3 captured goldens reproduce on deterministic
  fields with `MMR_DISABLE=1` (fails if the port perturbs the gated-off path).
  Green on base by construction (review S2 regression guard).
- test_threshold_marker_present — with `MMR_MODE=threshold MMR_TAU=0.95` stderr contains
  >=1 `[mmr-poc]` line (the only test that can fail on base: no port, no marker).
- test_marker_absent_under_disable — with `MMR_DISABLE=1` stderr contains zero `[mmr-poc]`
  lines (fails if the gate leaks). Green on base by construction (review S2).

Up to TWO `[mmr-poc]` lines per search is expected with the port (review C4:
`SearchResultMerge` and `AdjustMergedResults` both call `Merge`; the threshold filter is
idempotent — not a bug). The marker-present gate therefore asserts >=1, never an exact count.
"""
from __future__ import annotations

import json
import os
import select
import subprocess
import time
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
FIXTURES = REPO_ROOT / "scripts" / "retrieval_tuning" / "fixtures" / "poc-parity"
QUERIES_PATH = FIXTURES / "queries.json"
GOLDENS_PATH = FIXTURES / "base-goldens.json"

COPY_PATH = Path(os.environ["AI_RACCOON_EVAL_COPY"]) if os.environ.get("AI_RACCOON_EVAL_COPY") else None
DLL_PATH = Path(os.environ["AI_RACCOON_EVAL_DLL"]) if os.environ.get("AI_RACCOON_EVAL_DLL") else None
DATA_ROOT_OVERRIDE = os.environ.get("AI_RACCOON_EVAL_DATA_ROOT") or None

SESSION_TIMEOUT_S = 240.0
SESSION_ID = "poc-parity-1"


# ------------------------------------------------------------------ env gates / fixtures


@pytest.fixture(scope="session")
def eval_copy() -> Path:
    if COPY_PATH is None:
        pytest.skip("AI_RACCOON_EVAL_COPY not set — branch-only gate needs a bank copy "
                    "(make one with scripts/retrieval_tuning/make_memory_copy.py)")
    if not COPY_PATH.exists():
        pytest.skip(f"AI_RACCOON_EVAL_COPY points at a missing file: {COPY_PATH}")
    return COPY_PATH


@pytest.fixture(scope="session")
def eval_dll() -> Path:
    if DLL_PATH is None:
        pytest.skip("AI_RACCOON_EVAL_DLL not set — build one with `dotnet build src/AiRaccoon`")
    if not DLL_PATH.exists():
        pytest.skip(f"AI_RACCOON_EVAL_DLL points at a missing file: {DLL_PATH}")
    return DLL_PATH


@pytest.fixture(scope="session")
def eval_data_root(tmp_path_factory: pytest.TempPathFactory, eval_copy: Path) -> Path:
    """Data root for the server under test: the copy as `memory.db`, plus read-only
    symlinks to the live install's `models/` / `extensions/` so the embedding model
    resolves (mirrors scripts/retrieval_tuning/fixtures/poc-parity/README.md)."""
    if DATA_ROOT_OVERRIDE:
        root = Path(DATA_ROOT_OVERRIDE)
        if not (root / "memory.db").exists():
            pytest.skip(f"AI_RACCOON_EVAL_DATA_ROOT has no memory.db: {root}")
        return root
    root = tmp_path_factory.mktemp("ai-raccoon-eval-root")
    os.symlink(eval_copy, root / "memory.db")
    for name in ("models", "extensions"):
        src = Path.home() / ".ai-raccoon" / name
        if src.exists():
            os.symlink(src, root / name)
    return root


def _session_env(overrides: dict[str, str]) -> dict[str, str]:
    """Process env with every MMR_* variable stripped, then the caller's overrides —
    so a stray MMR_MODE in the invoking shell can never flip an arm."""
    env = {k: v for k, v in os.environ.items() if not k.startswith("MMR_")}
    env.update(overrides)
    return env


# ------------------------------------------------------------------ minimal MCP client


def _run_session(dll: Path, data_root: Path, env: dict[str, str], calls: list[dict],
                 stderr_path: Path) -> tuple[dict[int, dict], str]:
    """One server process, batched calls: initialize -> notifications/initialized ->
    tools/call memory_search per call. Interactive protocol (proven shape): each request is
    written with stdin kept OPEN and its response read before the next request — closing
    stdin early makes the stdio transport start shutdown and the in-flight responses are
    lost (verified this session: 'Application is shutting down' races the response write).
    Every read is select-bounded by SESSION_TIMEOUT_S overall, so a wedged server raises
    instead of hanging pytest. Returns ({response id: response}, stderr text)."""
    messages: list[dict] = [
        {"jsonrpc": "2.0", "id": 1, "method": "initialize",
         "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                    "clientInfo": {"name": "poc-port-gates", "version": "0"}}},
        {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}},
    ]
    for i, call in enumerate(calls, start=2):
        messages.append({"jsonrpc": "2.0", "id": i, "method": "tools/call", "params": call})

    responses: dict[int, dict] = {}
    buf = bytearray()
    with stderr_path.open("wb") as err:
        proc = subprocess.Popen(
            ["dotnet", str(dll), "--data-root", str(data_root), "--transport", "stdio"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=err, env=env)
        try:
            assert proc.stdin is not None and proc.stdout is not None
            fd = proc.stdout.fileno()
            deadline = time.monotonic() + SESSION_TIMEOUT_S

            def _send(message: dict) -> None:
                assert proc.stdin is not None
                proc.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
                proc.stdin.flush()

            def _read_response(want_id: int) -> dict:
                """Skip interleaved notifications / other ids; return the matching response."""
                nonlocal buf  # += below must mutate the shared line buffer, not rebind a local
                while True:
                    nl = buf.find(b"\n")
                    while nl != -1:
                        line = bytes(buf[:nl])
                        del buf[: nl + 1]
                        try:
                            msg = json.loads(line)
                        except json.JSONDecodeError:
                            continue
                        if isinstance(msg, dict) and msg.get("id") == want_id:
                            return msg
                        nl = buf.find(b"\n")
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise TimeoutError(
                            f"no response to id {want_id} within {SESSION_TIMEOUT_S:.0f}s")
                    ready, _, _ = select.select([fd], [], [], min(remaining, 5.0))
                    if not ready:
                        continue
                    chunk = os.read(fd, 65536)
                    if not chunk:
                        raise EOFError(
                            f"server closed stdout before responding to id {want_id}")
                    buf += chunk

            for message in messages:
                _send(message)
                if message.get("id") is not None:
                    responses[message["id"]] = _read_response(message["id"])

            # All responses collected — close stdin so the server exits, then drain.
            proc.stdin.close()
            shutdown_deadline = time.monotonic() + 60.0
            while proc.poll() is None and time.monotonic() < shutdown_deadline:
                ready, _, _ = select.select([fd], [], [], 0.5)
                if ready:
                    chunk = os.read(fd, 65536)
                    if not chunk:
                        break
                    buf += chunk
        finally:
            if proc.stdin is not None and not proc.stdin.closed:
                proc.stdin.close()
            if proc.poll() is None:
                proc.kill()
            proc.wait(timeout=30)

    return responses, stderr_path.read_text(encoding="utf-8", errors="replace")


def _memory_search_call(project: str, query: str, limit: int) -> dict:
    return {"name": "memory_search",
            "arguments": {"projectId": project, "query": query, "limit": limit,
                          "sessionId": SESSION_ID}}


def _extract_results(response: dict) -> dict:
    """response -> {memory: [...], code: [...]} on the deterministic fields only
    (timings stripped by construction). Same shape as the captured goldens."""
    content = response["result"]["content"][0]["text"]
    data = json.loads(content)["data"]
    out: dict[str, list] = {"memory": [], "code": []}
    for section, key in (("results", "memory"), ("code", "code")):
        for hit in data.get(section, []):
            out[key].append({
                "hash": hit["hash"],
                "ranking": hit["ranking"],
                "path": hit.get("path"),
                "sourceFile": hit.get("sourceFile"),
                "chunkIndex": hit.get("chunkIndex"),
                "snippet": (hit.get("snippet") or "")[:600],
            })
    return out


def _run_queries(dll: Path, data_root: Path, env: dict[str, str], stderr_path: Path,
                 queries: list[dict]) -> tuple[dict[str, dict], str]:
    calls = [_memory_search_call(q["project"], q["query"], q["limit"]) for q in queries]
    responses, stderr = _run_session(dll, data_root, env, calls, stderr_path)
    results: dict[str, dict] = {}
    for i, q in enumerate(queries, start=2):
        assert i in responses, f"{q['id']}: no JSON-RPC response (id {i}); server died early"
        results[q["id"]] = _extract_results(responses[i])
    return results, stderr


# ------------------------------------------------------------------ fixtures


def _load_queries() -> list[dict]:
    queries = json.loads(QUERIES_PATH.read_text(encoding="utf-8"))
    assert {q["id"] for q in queries} == {"q1", "q2", "q3"}
    return queries


@pytest.fixture(scope="session")
def goldens() -> dict:
    return json.loads(GOLDENS_PATH.read_text(encoding="utf-8"))


# ------------------------------------------------------------------ AC1.3: parity


def test_parity_vs_fixture_default_off(eval_dll: Path, eval_data_root: Path, goldens: dict,
                                       tmp_path: Path) -> None:
    """Default-off (MMR_DISABLE=1) replay of the 3 goldens must be byte-identical on the
    deterministic fields. Green on base by construction; fails if the port perturbs the
    gated-off path."""
    queries = _load_queries()
    results, _ = _run_queries(
        eval_dll, eval_data_root,
        _session_env({"MMR_DISABLE": "1"}),
        tmp_path / "parity.stderr", queries)
    for q in queries:
        got, want = results[q["id"]], goldens[q["id"]]
        assert (len(got["memory"]) + len(got["code"])) > 0, \
            f"{q['id']}: empty result set — parity against an empty set proves nothing"
        assert got == want, f"{q['id']}: default-off results diverge from the base goldens"


# ------------------------------------------------------------------ AC1.4: marker gates


def test_threshold_marker_present(eval_dll: Path, eval_data_root: Path, tmp_path: Path) -> None:
    """MMR_MODE=threshold MMR_TAU=0.95 must engage the diversifier: >=1 `[mmr-poc]` line on
    stderr. FAILS ON BASE (no port -> no marker) — this is the red-first check (review S2).
    Up to TWO lines per search are expected (review C4); never assert an exact count."""
    queries = [q for q in _load_queries() if q["id"] == "q1"]  # one non-path query
    _, stderr = _run_queries(
        eval_dll, eval_data_root,
        _session_env({"MMR_MODE": "threshold", "MMR_TAU": "0.95"}),
        tmp_path / "threshold.stderr", queries)
    marker_lines = [ln for ln in stderr.splitlines() if "[mmr-poc]" in ln]
    assert marker_lines, (
        "no [mmr-poc] marker on stderr under MMR_MODE=threshold — the diversifier did not "
        "engage (port missing, env gate leaking, or the marker was removed)")
    assert any("mode=threshold" in ln for ln in marker_lines), (
        f"[mmr-poc] lines present but none says mode=threshold: {marker_lines}")


def test_marker_absent_under_disable(eval_dll: Path, eval_data_root: Path, tmp_path: Path) -> None:
    """MMR_DISABLE=1 must keep the diversifier fully silent: zero `[mmr-poc]` lines.
    Green on base by construction; fails if the gate leaks (review S2 regression guard)."""
    queries = [q for q in _load_queries() if q["id"] == "q1"]
    _, stderr = _run_queries(
        eval_dll, eval_data_root,
        _session_env({"MMR_DISABLE": "1"}),
        tmp_path / "disable.stderr", queries)
    leaks = [ln for ln in stderr.splitlines() if "[mmr-poc]" in ln]
    assert not leaks, f"MMR_DISABLE=1 but the PoC marker leaked: {leaks}"
