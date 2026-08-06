"""Slow integration tests: real ai-raccoon server spawned against a temp bank.

Run with --run-slow. Skips when the ai-raccoon binary is not installed.
The server child inherits the environment with AIRACCOON_DATA_ROOT pointed
at a temp dir, so the real ~/.ai-raccoon bank is never touched.
"""

from __future__ import annotations

import json
import shutil

import pytest

pytestmark = pytest.mark.slow


def _binary() -> str:
    path = shutil.which("ai-raccoon")
    if not path:
        pytest.skip("ai-raccoon binary not on PATH")
    assert path is not None
    return path


@pytest.fixture
def real_provider(provider_module, tmp_path, monkeypatch):
    monkeypatch.setenv("AIRACCOON_DATA_ROOT", str(tmp_path / "bank"))
    provider = provider_module.AiRaccoonMemoryProvider(
        config={"transport": "stdio", "binary": _binary()}
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
        pytest.skip("server failed to spawn")
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
