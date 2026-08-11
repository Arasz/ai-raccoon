"""Behavior tests for the caller-side observability: status words.

The server cannot attribute calls to a client; the provider emits one-word
status cues on stderr. The JSONL operation log (AIRACCOON_MEMORY_LOG) was
removed 2026-08-11 (task mem-cleanup) — the env var is inert.
"""

from __future__ import annotations

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


def test_no_operation_log_file_created_when_env_set(make_provider, tmp_path, monkeypatch):
    """File-based op logging is removed: AIRACCOON_MEMORY_LOG is inert."""
    log_path = tmp_path / "memory-log.jsonl"
    monkeypatch.setenv("AIRACCOON_MEMORY_LOG", str(log_path))
    provider, fake = make_provider()
    _init(provider, session_id="abc", agent_identity="coder")
    fake.search_results = {"results": [{"snippet": "x", "ranking": 0.9}]}

    provider.prefetch("q", session_id="abc")
    provider.handle_tool_call("memory_stats", {})

    assert not log_path.exists()


def test_all_provider_tools_have_status_words(provider_module, status_module):
    """Drift guard: every tool the provider can dispatch or emit for must have a word."""
    tools = set(provider_module._TOOL_DISPATCH) | {"memory_search", "memory_write"}
    missing = [t for t in tools if t not in status_module.STATUS_WORDS]
    assert missing == [], f"tools without status words: {missing}"
