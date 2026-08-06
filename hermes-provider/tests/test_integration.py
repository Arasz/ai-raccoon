"""Slow integration tests: real ai-raccoon server spawned against a temp bank.

Run with --run-slow. Skips when the ai-raccoon binary is not installed.
Isolation is real: the server child is spawned with ``--data-root <tmp>``
(the production CLI resolves the data root ONLY from that flag — the
AIRACCOON_DATA_ROOT env var is a test-host-only construct), so the real
~/.ai-raccoon bank is never touched.
"""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import time

import pytest

pytestmark = pytest.mark.slow

_LISTENING_RE = re.compile(r"http transport listening on (http://\S+)")


def _binary() -> str:
    path = shutil.which("ai-raccoon")
    if not path:
        pytest.skip("ai-raccoon binary not on PATH")
    assert path is not None
    return path


@pytest.fixture
def real_provider(provider_module, tmp_path, request):
    binary = _binary()
    provider = provider_module.AiRaccoonMemoryProvider(
        config={"transport": "stdio", "binary": binary,
                "binary_args": ["--data-root", str(tmp_path / "bank")]}
    )
    provider.initialize(
        "itest",
        hermes_home=str(tmp_path),
        platform="cli",
        agent_context="primary",
        agent_identity="itest",
        agent_workspace="hermes",
    )
    if provider._client is None:
        # --run-slow was explicitly requested: a broken spawn is a failure,
        # not a skip. Only the binary-missing case (above) is a skip.
        pytest.fail(f"server failed to spawn: {binary}")
    yield provider
    provider.shutdown()


def test_stdio_write_search_round_trip(real_provider):
    write_result = json.loads(real_provider.handle_tool_call(
        "memory_write", {"content": "integration probe fact 12345",
                         "sourceFile": "probe.md", "section": "test"}))
    assert "hash" in write_result and write_result["hash"]

    search_result = json.loads(real_provider.handle_tool_call(
        "memory_search", {"query": "integration probe fact", "limit": 5, "minScore": 0.0}))
    snippets = [r.get("snippet", "") for r in search_result.get("results", [])]
    assert any("integration probe fact 12345" in s for s in snippets), snippets


def test_prefetch_returns_block_with_hit(real_provider):
    real_provider.handle_tool_call(
        "memory_write", {"content": "prefetch probe marker 777", "sourceFile": "probe.md"})
    block = real_provider.prefetch("prefetch probe marker", session_id="itest")
    assert block.startswith("## AiRaccoon Memory")
    assert "prefetch probe marker 777" in block


def test_stats_counts_entries(real_provider):
    real_provider.handle_tool_call("memory_write", {"content": "stats probe entry 999"})
    stats = json.loads(real_provider.handle_tool_call("memory_stats", {}))
    assert stats["entries"] >= 1
    assert "pending" in stats


def test_sync_turn_writes_and_search_finds_it(real_provider):
    real_provider.sync_turn("user asks", "assistant says sync probe 555", session_id="itest")
    real_provider._join_sync_threads()
    block = real_provider.prefetch("sync probe", session_id="itest")
    assert "sync probe 555" in block


def test_shutdown_terminates_child_and_is_idempotent(real_provider):
    client = real_provider._client
    real_provider.shutdown()
    real_provider.shutdown()
    assert client._session is None
    assert client._thread is None or not client._thread.is_alive()


def test_http_transport_smoke(tmp_path, client_module):
    """HTTP path: spawn a server on a random port, connect the HttpClient, call stats."""
    binary = _binary()
    proc = subprocess.Popen(
        [binary, "--transport", "http", "--port", "0", "--data-root", str(tmp_path / "httpbank")],
        stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True,
    )
    assert proc.stderr is not None
    url = None
    deadline = time.monotonic() + 30
    try:
        while time.monotonic() < deadline:
            line = proc.stderr.readline()
            if not line:
                if proc.poll() is not None:
                    pytest.fail(f"server exited early: {proc.returncode}")
                continue
            match = _LISTENING_RE.search(line)
            if match:
                url = match.group(1)
                break
        if not url:
            pytest.fail("server never reported its listening URL")

        client = client_module.HttpClient(url)
        client.connect()
        try:
            stats = client.stats("smoke")
            assert "entries" in stats and stats["entries"] == 0
        finally:
            client.close()
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
