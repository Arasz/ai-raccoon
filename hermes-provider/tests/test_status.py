"""Behavior tests for the caller-side observability: status words + operation log.

The server cannot attribute calls to a client; the provider emits one-word
status cues on stderr and JSONL rows (with agent/session attribution) to the
AIRACCOON_MEMORY_LOG file.
"""

from __future__ import annotations

import json

import pytest


def _init(provider, session_id="s1", **kwargs):
    base = dict(hermes_home="/tmp/fake-home", platform="cli",
                agent_context="primary", agent_identity="default",
                agent_workspace="hermes")
    base.update(kwargs)
    provider.initialize(session_id, **base)


def test_status_word_mapping(status_module):
    assert status_module.status_word("memory_write") == "remembering"
    assert status_module.status_word("memory_search") == "searching"
    assert status_module.status_word("memory_delete") == "forgetting"
    assert status_module.status_word("memory_watch_add") == "watching"
    assert status_module.status_word("memory_foo") == "foo"
    assert status_module.status_word("memory") == "working"


def test_prefetch_emits_searching_word(make_provider, capsys):
    provider, fake = make_provider()
    _init(provider)
    fake.search_results = {"results": [{"snippet": "x", "ranking": 0.9}]}
    provider.prefetch("q", session_id="s1")
    captured = capsys.readouterr()
    assert captured.err.strip() == "searching"


def test_handle_tool_call_emits_word_for_tool(make_provider, capsys):
    provider, fake = make_provider()
    _init(provider)
    provider.handle_tool_call("memory_stats", {})
    assert capsys.readouterr().err.strip() == "counting"


def test_status_words_disabled_prints_nothing(make_provider, capsys):
    provider, fake = make_provider(config={"status_words": False})
    _init(provider)
    provider.handle_tool_call("memory_stats", {})
    assert capsys.readouterr().err.strip() == ""


def test_operation_log_rows_carry_caller_attribution(make_provider, tmp_path, monkeypatch):
    log_path = tmp_path / "memory-log.jsonl"
    monkeypatch.setenv("AIRACCOON_MEMORY_LOG", str(log_path))
    provider, fake = make_provider()
    _init(provider, session_id="abc", agent_identity="coder")
    fake.search_results = {"results": [{"snippet": "x", "ranking": 0.9}]}

    provider.prefetch("q", session_id="abc")
    provider.handle_tool_call("memory_stats", {})

    rows = [json.loads(l) for l in log_path.read_text().splitlines()]
    assert len(rows) == 2
    assert [r["tool"] for r in rows] == ["memory_search", "memory_stats"]
    assert all(r["status"] == "ok" for r in rows)
    assert all(r["project_id"] == "hermes-coder" for r in rows)
    assert all(r["agent_id"] == "hermes-coder" for r in rows)
    assert all(r["session_id"] == "abc" for r in rows)
    assert all("ts" in r and "duration_ms" in r for r in rows)


def test_operation_log_records_errors(make_provider, tmp_path, monkeypatch):
    log_path = tmp_path / "memory-log.jsonl"
    monkeypatch.setenv("AIRACCOON_MEMORY_LOG", str(log_path))
    provider, fake = make_provider()

    def boom(project_id, query, **kwargs):
        raise RuntimeError("server unreachable")

    fake.search = boom
    _init(provider, session_id="abc")
    provider.prefetch("q", session_id="abc")

    rows = [json.loads(l) for l in log_path.read_text().splitlines()]
    assert len(rows) == 1
    assert rows[0]["status"] == "error"
    assert rows[0]["error_type"] == "RuntimeError"


def test_operation_log_not_created_without_env(make_provider, tmp_path, monkeypatch):
    monkeypatch.delenv("AIRACCOON_MEMORY_LOG", raising=False)
    provider, fake = make_provider()
    _init(provider)
    provider.handle_tool_call("memory_stats", {})
    assert not (tmp_path / "memory-log.jsonl").exists()
