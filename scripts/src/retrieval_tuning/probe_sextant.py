"""Pinned sextant probe (plan §4, gate G1, WP1).

Runs the 7 probe queries against the 9-entry sextant bank at DEFAULT config
(all 9 retrieval knobs written explicitly to the scratch server's settings —
never inherited state) and asserts the observed top-5 hashes against the
tables recorded in docs/work/2026-08-20-hybrid-retrieval-fusion-investigation.md.

The probe guards against silent pipeline drift: any mismatch between the
recorded expectations and the live rankings fails the run (non-zero exit)
with a per-position message naming the expected and observed hash.

Safety (plan C2): the server always runs on a COPY of the bank under
/tmp/continue-testing-algorithm/runs/<date>/sextant-probe/, binds --port 0,
and the bound port is asserted != 7721; the data root is asserted !=
~/.ai-raccoon.

Run:  python scripts/retrieval_tuning/probe_sextant.py --binary <bin> \
            --bank /tmp/continue-testing-algorithm/datasets/sextant-bank
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from datetime import datetime

import httpx

from retrieval_tuning.matrix import DEFAULTS

# ---------------------------------------------------------------------------
# Corpus identity — investigation doc appendix (2026-08-20, 9 entries).
# ---------------------------------------------------------------------------

LABEL_TO_HASH: dict[str, str] = {
    "astrolabe": "61dfec67",
    "invoice": "c7053da1",
    "signal-15": "27e3ce27",
    "review-note": "9648f7e6",
    "guide-intro": "bd1587a2",
    "guide-details": "93f41fe5",
    "cross-project": "b032ac63",
    "sextant": "80e36737",
    "notes-digest": "97948a1a",
}

PROBE_QUERIES: list[tuple[str, str]] = [
    ("astrolabe-original",
     "an antique navigation instrument reflecting evening light in a stargazing room"),
    ("astrolabe-richer",
     "brass astronomical measuring device from the seventeenth century"),
    ("sextant-zero-overlap",
     "a navigator's device for measuring angles to celestial bodies at night"),
    ("sextant-richer",
     "sailor's tool to sight the height of stars above the waves"),
    ("invoice",
     "how much was the invoice for office supplies"),
    ("widget-guide",
     "widget pipeline onboarding guide for tenants"),
    ("alien-tokens",
     "quantum espresso froth ratios"),
]

# Recorded default-config (fusion OFF) expectations. Each pin is either
# {"type": "hash", "hash": "<8-char prefix>"} — directly recorded, or a label
# pin {"type": "label", "label": ...} resolved through LABEL_TO_HASH — or an
# explicit {"type": "unresolved", ...} sentinel for a position the doc records
# only as a label that the corpus appendix cannot resolve ("near-miss").
# Sources: "investigation §4" = the Query A contrast table + the fusion A/B
# table (OFF column, same corpus, default config); "investigation §3a" = the
# full Query B hash table.
EXPECTATIONS: dict[str, dict] = {
    "astrolabe-original": {
        "source": "investigation §4 'default' row: astrolabe, guide-intro, near-miss, guide-details, sextant — "
                 "rank 3 recorded as the 'near-miss' label; resolved to review-note (9648f7e6) by the first "
                 "live probe run 2026-08-21 (all other positions matched)",
        "pins": {
            1: {"type": "label", "label": "astrolabe"},
            2: {"type": "label", "label": "guide-intro"},
            3: {"type": "hash", "hash": "9648f7e6"},
            4: {"type": "label", "label": "guide-details"},
            5: {"type": "label", "label": "sextant"},
        },
    },
    "astrolabe-richer": {
        "source": "investigation §4 fusion A/B OFF column: details, intro, astrolabe (top-3 recorded)",
        "pins": {
            1: {"type": "label", "label": "guide-details"},
            2: {"type": "label", "label": "guide-intro"},
            3: {"type": "label", "label": "astrolabe"},
        },
    },
    "sextant-zero-overlap": {
        "source": "investigation §3a default-config hash table (limit 10), top-5",
        "pins": {
            1: {"type": "hash", "hash": "bd1587a2"},
            2: {"type": "hash", "hash": "93f41fe5"},
            3: {"type": "hash", "hash": "61dfec67"},
            4: {"type": "hash", "hash": "80e36737"},
            5: {"type": "hash", "hash": "b032ac63"},
        },
    },
    "sextant-richer": {
        "source": "investigation §4 fusion A/B OFF column: intro, details, sextant (top-3 recorded)",
        "pins": {
            1: {"type": "label", "label": "guide-intro"},
            2: {"type": "label", "label": "guide-details"},
            3: {"type": "label", "label": "sextant"},
        },
    },
    "invoice": {
        "source": "investigation §4 fusion A/B OFF column: intro, details, invoice (top-3 recorded)",
        "pins": {
            1: {"type": "label", "label": "guide-intro"},
            2: {"type": "label", "label": "guide-details"},
            3: {"type": "label", "label": "invoice"},
        },
    },
    "widget-guide": {
        "source": "inferred, NOT recorded: guide-intro is the doc-recorded top-1 for every recorded "
                 "query family (sibling boost + FTS overlap); checklist anchor item proves guide.md#intro -> bd1587a2",
        "pins": {
            1: {"type": "label", "label": "guide-intro"},
        },
    },
    "alien-tokens": {
        "source": "investigation §4 fusion A/B OFF column: details, intro, sextant (top-3 recorded)",
        "pins": {
            1: {"type": "label", "label": "guide-details"},
            2: {"type": "label", "label": "guide-intro"},
            3: {"type": "label", "label": "sextant"},
        },
    },
}

DEFAULT_LIMIT = 10  # recorded tables came from limit-10 runs; top-5 is asserted
EXPECTED_ENTRY_COUNT = 9


class SafetyError(RuntimeError):
    """A scratch-safety assertion failed (plan C2)."""


def assert_scratch_safety(port: int, data_root: str) -> None:
    """Refuse to operate on the live bank or the live port (plan C2/G7)."""
    if port == 7721:
        raise SafetyError(f"bound port {port} is the live server port — refusing to probe")
    live_bank = os.path.realpath(os.path.expanduser("~/.ai-raccoon"))
    if os.path.realpath(data_root) == live_bank:
        raise SafetyError(f"data-root {data_root} is the live bank — refusing to probe")


def parse_bound_port(serve_log: str) -> int:
    """Extract the bound port from a serve log ('Now listening on: http://127.0.0.1:PORT')."""
    match = re.search(r"Now listening on: http://127\.0\.0\.1:(\d+)", serve_log)
    if not match:
        raise RuntimeError("serve log does not report a bound port ('Now listening on: ...' missing)")
    return int(match.group(1))


def resolve_expected(exp: dict) -> dict[int, tuple[str, str | None]]:
    """Resolve an expectation's pins to (position, expected-hash-or-None)."""
    resolved: dict[int, tuple[str, str | None]] = {}
    for position, pin in exp["pins"].items():
        if pin["type"] == "hash":
            resolved[position] = (str(position), pin["hash"])
        elif pin["type"] == "label":
            resolved[position] = (str(position), LABEL_TO_HASH[pin["label"]])
        else:  # unresolved sentinel — fails by design until pinned
            resolved[position] = (str(position), None)
    return resolved


def match_top5(observed: list[str], exp: dict) -> list[dict]:
    """Compare observed 8-char hash prefixes against the expectation's pins.

    Only pinned positions are asserted; positions the doc did not record are
    reported by the caller but never fail the probe.
    """
    mismatches: list[dict] = []
    for position, (_, expected) in resolve_expected(exp).items():
        if position > len(observed):
            continue
        observed_hash = observed[position - 1]
        if expected is None:
            mismatches.append({
                "position": position,
                "expected": None,
                "observed": observed_hash,
                "message": (f"position {position} is the doc's '{exp['pins'][position]['label']}' label, "
                            f"not resolved to a hash — observed {observed_hash}; pin it after the first live run"),
            })
        elif observed_hash != expected:
            mismatches.append({
                "position": position,
                "expected": expected,
                "observed": observed_hash,
                "message": f"position {position}: expected {expected}, observed {observed_hash}",
            })
    return mismatches


# ---------------------------------------------------------------------------
# Live server + MCP plumbing (scratch only).
# ---------------------------------------------------------------------------

def copy_bank(source_bank: str, dest_bank: str) -> None:
    """Copy the bank dir (never use the source file directly)."""
    os.makedirs(os.path.dirname(dest_bank), exist_ok=True)
    if os.path.exists(dest_bank):
        shutil.rmtree(dest_bank)
    shutil.copytree(source_bank, dest_bank)


def start_server(binary: str, data_root: str, log_path: str) -> subprocess.Popen:
    """Start `serve --port 0` on a scratch data root; returns the process."""
    with open(log_path, "w") as fh:
        proc = subprocess.Popen(
            [binary, "--data-root", data_root, "serve", "--port", "0", "--idle-timeout", "0"],
            stdout=fh, stderr=subprocess.STDOUT, start_new_session=True,
        )
    return proc


def wait_for_port(log_path: str, timeout_s: float = 90.0) -> int:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        try:
            with open(log_path) as fh:
                log = fh.read()
        except FileNotFoundError:
            log = ""
        if "Now listening on:" in log:
            return parse_bound_port(log)
        time.sleep(0.5)
    raise RuntimeError(f"server did not report a bound port within {timeout_s}s (log: {log_path})")


def stop_server(proc: subprocess.Popen) -> None:
    """SIGTERM the server's process group and wait for it to exit."""
    if proc.poll() is not None:
        return
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGTERM)
    except ProcessLookupError:
        return
    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=5)


def run_cli(binary: str, data_root: str, port: int, *args: str) -> str:
    """Run a server-routed CLI verb (settings/model) against the scratch server."""
    cmd = [binary, "--data-root", data_root, "--port", str(port), *args]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
    if result.returncode != 0:
        raise RuntimeError(f"CLI failed ({result.returncode}): {' '.join(cmd)}\n"
                           f"stdout: {result.stdout}\nstderr: {result.stderr}")
    return result.stdout


def apply_default_settings(binary: str, data_root: str, port: int) -> str:
    """Write all 9 retrieval defaults explicitly (plan §3.1: never inherited state)."""
    verbs = {
        "rrfK": ("rrfk", str(DEFAULTS["rrfK"])),
        "ftsWeight": ("fts-weight", str(DEFAULTS["ftsWeight"])),
        "vectorWeight": ("vector-weight", str(DEFAULTS["vectorWeight"])),
        "sourceLambda": ("source-lambda", str(DEFAULTS["sourceLambda"])),
        "consolidationThreshold": ("consolidation", str(DEFAULTS["consolidationThreshold"])),
        "docScoreFormula": ("doc-formula", str(DEFAULTS["docScoreFormula"])),
        "candidateWindow": ("window", str(DEFAULTS["candidateWindow"])),
        "structureAlpha": ("alpha", str(DEFAULTS["structureAlpha"])),
    }
    for verb, value in verbs.values():
        run_cli(binary, data_root, port, "settings", "retrieval", verb, "set", value)
    run_cli(binary, data_root, port, "settings", "retrieval", "fusion", "disable")
    return run_cli(binary, data_root, port, "settings", "retrieval", "show-all")


class McpClient:
    """Minimal streamable-HTTP JSON-RPC client (pattern: scripts/src/mcp_client.py)."""

    def __init__(self, base_url: str, token: str) -> None:
        self._base = base_url.rstrip("/")
        self._headers = {
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        }
        self._req_id = 0

    def _rpc(self, method: str, params: dict) -> dict:
        self._req_id += 1
        body = {"jsonrpc": "2.0", "id": self._req_id, "method": method, "params": params}
        with httpx.Client(timeout=120) as client:
            resp = client.post(f"{self._base}/mcp", headers=self._headers, json=body)
            resp.raise_for_status()
            text = resp.text.strip()
            data_lines = [ln[5:].strip() for ln in text.splitlines() if ln.startswith("data:")]
            payload = json.loads(data_lines[-1]) if data_lines else json.loads(text)
        return payload

    def initialize(self) -> None:
        self._rpc("initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "probe-sextant", "version": "1.0"},
        })
        self._rpc("notifications/initialized", {})

    def call(self, tool: str, arguments: dict) -> dict:
        result = self._rpc("tools/call", {"name": tool, "arguments": arguments})
        if "result" not in result or result["result"].get("isError"):
            raise RuntimeError(f"tool {tool} failed: {json.dumps(result)[:400]}")
        text = next(item["text"] for item in result["result"]["content"] if item.get("type") == "text")
        return json.loads(text)

    def memory_search(self, project_id: str, query: str, limit: int) -> list[dict]:
        data = self.call("memory_search", {
            "projectId": project_id, "query": query, "limit": limit, "minRelativeScore": 0.0,
        })
        return data["data"]["results"]

    def memory_stats(self, project_id: str) -> dict:
        return self.call("memory_stats", {"projectId": project_id})["data"]


def wait_for_embed_ready(client: McpClient, project_id: str, timeout_s: float = 90.0) -> dict:
    """Wait for the re-embed triggered by `model embedding set local` to drain."""
    deadline = time.monotonic() + timeout_s
    last: dict = {}
    while time.monotonic() < deadline:
        try:
            last = client.memory_stats(project_id)
        except RuntimeError:
            time.sleep(2)  # server blocks tool calls while re-embedding
            continue
        pending = last.get("pending", 0)
        if pending == 0:
            return last
        time.sleep(2)
    raise RuntimeError(f"re-embed did not drain within {timeout_s}s; last stats: {last}")


def _print_results(label: str, results: list[dict]) -> None:
    print(f"--- {label}")
    for i, h in enumerate(results, start=1):
        src = h.get("sourceFile") or h.get("path") or "-"
        chunk = f"{h.get('chunkIndex')}/{h.get('totalChunks')}" if h.get("chunkIndex") is not None else ""
        snippet = (h.get("snippet") or "")[:64].replace("\n", " ")
        print(f"  {i:>2}. {h['hash'][:8]} rank={h.get('ranking', float('nan')):.4f} "
              f"{chunk:>5} src={src[:36]:36} :: {snippet}")
    print()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Pinned sextant probe (plan §4).")
    parser.add_argument("--binary", required=True,
                        help="ai-raccoon binary (must expose the 9 settings verbs)")
    parser.add_argument("--bank", default="/tmp/continue-testing-algorithm/datasets/sextant-bank",
                        help="Source sextant bank dir (read-only input; a copy is served)")
    parser.add_argument("--runs-root", default="/tmp/continue-testing-algorithm/runs",
                        help="Scratch root; the run goes to <runs-root>/<date>/sextant-probe/")
    parser.add_argument("--project-id", default="checklist-1-27-2")
    parser.add_argument("--limit", type=int, default=DEFAULT_LIMIT)
    args = parser.parse_args(sys.argv[1:] if argv is None else argv)

    run_dir = os.path.join(args.runs_root, datetime.now().strftime("%Y-%m-%d"), "sextant-probe")
    bank_copy = os.path.join(run_dir, "bank")
    serve_log = os.path.join(run_dir, "serve.log")
    os.makedirs(run_dir, exist_ok=True)

    assert_scratch_safety(port=0, data_root=bank_copy)
    copy_bank(args.bank, bank_copy)

    proc: subprocess.Popen | None = None
    try:
        before = subprocess.run(["lsof", "-i", ":7721", "-sTCP:LISTEN"],
                                capture_output=True, text=True).stdout.strip() or "(nothing listening)"
        print(f"lsof 7721 BEFORE: {before}")

        proc = start_server(args.binary, bank_copy, serve_log)
        port = wait_for_port(serve_log)
        assert_scratch_safety(port=port, data_root=bank_copy)  # hard: port != 7721
        print(f"scratch server bound: http://127.0.0.1:{port} (pid {proc.pid})")

        token_path = os.path.join(bank_copy, "mcp-token")
        with open(token_path) as fh:
            token = fh.read().strip()
        client = McpClient(f"http://127.0.0.1:{port}", token)
        client.initialize()

        print("setting embedding engine: model embedding set local")
        run_cli(args.binary, bank_copy, port, "model", "embedding", "set", "local")
        stats = wait_for_embed_ready(client, args.project_id)
        entry_count = stats.get("entries")
        print(f"memory_stats: entries={entry_count} pending={stats.get('pending')}")
        if entry_count != EXPECTED_ENTRY_COUNT:
            print(f"CORPUS DRIFT: expected {EXPECTED_ENTRY_COUNT} entries, got {entry_count}")
            return 1

        print("writing all 9 retrieval defaults explicitly:")
        print(apply_default_settings(args.binary, bank_copy, port))

        all_mismatches: list[dict] = []
        for qid, query in PROBE_QUERIES:
            results = client.memory_search(args.project_id, query, args.limit)
            observed = [h["hash"][:8] for h in results[:5]]
            _print_results(qid, results)
            mismatches = match_top5(observed, EXPECTATIONS[qid])
            all_mismatches.extend({"query": qid, **m} for m in mismatches)

        print("=" * 78)
        print("ASSERTION RESULT (default config top-5 vs investigation doc):")
        if all_mismatches:
            for m in all_mismatches:
                print(f"  FAIL {m['query']}: {m['message']}")
            print("probe FAILED — recorded expectations do not match the live rankings")
            return 1
        print("  all pinned positions matched the recorded expectations")
        print("probe PASSED — no silent pipeline drift detected")
        return 0
    finally:
        if proc is not None and proc.poll() is None:
            stop_server(proc)
        after = subprocess.run(["lsof", "-i", ":7721", "-sTCP:LISTEN"],
                               capture_output=True, text=True).stdout.strip() or "(nothing listening)"
        print(f"lsof 7721 AFTER:  {after}")


if __name__ == "__main__":
    sys.exit(main())
