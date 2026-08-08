"""Behavior tests for the ai-raccoon Hermes memory provider (RED first)."""

from __future__ import annotations

import json
import pytest
import shutil

CURATED_TOOLS = {"memory_search", "memory_write", "memory_stats", "memory_share"}


def _init(provider, session_id="s1", **kwargs):
    base = dict(hermes_home="/tmp/fake-home", platform="cli",
                agent_context="primary", agent_identity="default",
                agent_workspace="hermes")
    base.update(kwargs)
    provider.initialize(session_id, **base)


# -- identity ---------------------------------------------------------------

def test_name_is_ai_raccoon(make_provider):
    provider, _ = make_provider()
    assert provider.name == "ai-raccoon"


def test_is_available_stdio_with_resolvable_binary(make_provider, monkeypatch):
    monkeypatch.setattr(shutil, "which", lambda cmd: f"/fake/bin/{cmd}")
    provider, _ = make_provider({"transport": "stdio", "binary": "ai-raccoon"})
    assert provider.is_available() is True


def test_is_available_stdio_missing_binary(make_provider, monkeypatch):
    monkeypatch.setattr(shutil, "which", lambda cmd: None)
    provider, _ = make_provider({"transport": "stdio", "binary": "ai-raccoon"})
    assert provider.is_available() is False


def test_is_available_http_with_url(make_provider):
    provider, _ = make_provider({"transport": "http", "url": "http://127.0.0.1:7721/mcp"})
    assert provider.is_available() is True


def test_is_available_makes_no_network_or_client_construction(make_provider, monkeypatch):
    """is_available must never construct the client (ABC: no network calls)."""
    monkeypatch.setattr(shutil, "which", lambda cmd: f"/fake/bin/{cmd}")

    def factory(cfg):
        raise AssertionError("client factory must not run during is_available()")

    provider = make_provider({"transport": "stdio"})[0]
    provider._client_factory = factory
    provider.is_available()


def test_project_id_derived_from_identity_and_workspace(make_provider):
    provider, _ = make_provider()
    _init(provider, agent_identity="coder", agent_workspace="hermes")
    assert provider.project_id == "hermes-coder"


def test_project_id_defaults_to_hermes_default(make_provider):
    provider, _ = make_provider()
    _init(provider)
    assert provider.project_id == "hermes-default"


def test_project_id_config_override_wins(make_provider):
    provider, _ = make_provider({"project_id": "my-project"})
    _init(provider, agent_identity="coder")
    assert provider.project_id == "my-project"


# -- lifecycle --------------------------------------------------------------

def test_initialize_creates_client_and_derives_project(make_provider):
    provider, fake = make_provider()
    _init(provider, session_id="abc", agent_identity="coder")
    assert provider.project_id == "hermes-coder"


def test_initialize_failure_is_graceful(make_provider):
    provider, _ = make_provider()

    def factory(cfg):
        raise RuntimeError("server unreachable")

    provider._client_factory = factory
    _init(provider)  # must not raise
    assert provider._client is None


def test_shutdown_closes_client_and_is_idempotent(make_provider):
    provider, fake = make_provider()
    _init(provider)
    provider.shutdown()
    provider.shutdown()
    assert fake.closed is True


def test_shutdown_without_client_does_not_raise(make_provider):
    provider, _ = make_provider()
    provider.shutdown()


# -- system prompt ----------------------------------------------------------

def test_system_prompt_block_teaches_memory_first(make_provider):
    provider, _ = make_provider()
    block = provider.system_prompt_block()
    assert "memory_search" in block
    assert "memory_write" in block


# -- prefetch ---------------------------------------------------------------

def test_prefetch_formats_hits_as_block(make_provider):
    provider, fake = make_provider()
    _init(provider)
    fake.search_results = {"results": [
        {"snippet": "first remembered fact", "ranking": 0.9},
        {"snippet": "second remembered fact", "ranking": 0.6},
    ]}
    block = provider.prefetch("what do we know about x", session_id="s1")
    assert block.startswith("## AiRaccoon Memory")
    assert "first remembered fact" in block
    assert "second remembered fact" in block
    assert "0.9" in block
    assert fake.searches[0]["project_id"] == "hermes-default"
    assert fake.searches[0]["limit"] == 5


def test_prefetch_empty_when_no_hits(make_provider):
    provider, fake = make_provider()
    _init(provider)
    fake.search_results = {"results": []}
    assert provider.prefetch("nothing here") == ""


def test_prefetch_without_client_returns_empty(make_provider):
    provider, _ = make_provider()
    assert provider.prefetch("anything") == ""


# -- sync_turn --------------------------------------------------------------

def test_sync_turn_writes_assistant_message(make_provider):
    provider, fake = make_provider()
    _init(provider, session_id="abc")
    provider.sync_turn("user said", "assistant answer", session_id="abc")
    provider._join_sync_threads()
    assert len(fake.writes) == 1
    w = fake.writes[0]
    assert w["content"] == "assistant answer"
    assert w["source_file"] == "hermes/abc"
    assert w["section"] == "turn"
    assert w["agent_id"] == "hermes-default"
    assert w["project_id"] == "hermes-default"


def test_sync_turn_without_client_does_not_raise(make_provider):
    provider, _ = make_provider()
    provider.sync_turn("u", "a")
    provider._join_sync_threads()


def test_sync_turn_skipped_for_non_primary_context(make_provider):
    provider, fake = make_provider()
    _init(provider, agent_context="cron")
    provider.sync_turn("u", "a")
    provider._join_sync_threads()
    assert fake.writes == []


def test_sync_turn_is_non_blocking(make_provider, fake_client):
    import time

    def slow_write(**kwargs):
        time.sleep(0.4)
        return {"hash": "h"}

    fake_client.write = slow_write
    provider, fake = make_provider(fake=fake_client)
    _init(provider)
    start = time.monotonic()
    provider.sync_turn("u", "a")
    elapsed = time.monotonic() - start
    provider._join_sync_threads()
    assert elapsed < 0.2, f"sync_turn blocked for {elapsed:.2f}s"


# -- tool surface -----------------------------------------------------------

def test_get_tool_schemas_exposes_four_curated_tools(make_provider):
    provider, _ = make_provider()
    schemas = provider.get_tool_schemas()
    names = {s["name"] for s in schemas}
    assert names == CURATED_TOOLS
    for s in schemas:
        assert set(s) >= {"name", "description", "parameters"}


def test_tool_schemas_do_not_expose_project_id(make_provider):
    provider, _ = make_provider()
    for s in provider.get_tool_schemas():
        props = s["parameters"].get("properties", {})
        assert "projectId" not in props
        assert "projectId" not in s["parameters"].get("required", [])


def test_handle_tool_call_injects_project_id_and_returns_json(make_provider):
    provider, fake = make_provider()
    _init(provider)
    fake.search_results = {"results": [{"snippet": "hit", "ranking": 0.8}]}
    result = provider.handle_tool_call("memory_search", {"query": "q", "limit": 3})
    payload = json.loads(result)
    assert payload == fake.search_results
    assert fake.searches[0]["project_id"] == "hermes-default"
    assert fake.searches[0]["query"] == "q"


def test_handle_tool_call_stats_and_share(make_provider):
    provider, fake = make_provider()
    _init(provider)
    provider.handle_tool_call("memory_stats", {})
    provider.handle_tool_call("memory_share", {"hash": "abc"})
    assert fake.stats_calls == 1
    assert fake.shares[0]["hash"] == "abc"


def test_handle_tool_call_unknown_tool_returns_json_error(make_provider):
    provider, _ = make_provider()
    result = provider.handle_tool_call("memory_sweep", {})
    payload = json.loads(result)
    assert "error" in payload


def test_handle_tool_call_without_client_returns_json_error(make_provider):
    provider, _ = make_provider()
    result = provider.handle_tool_call("memory_search", {"query": "q"})
    assert "error" in json.loads(result)


# -- on_memory_write --------------------------------------------------------

def test_on_memory_write_add_mirrors_to_source_hermes_memory(make_provider):
    provider, fake = make_provider()
    _init(provider)
    provider.on_memory_write("add", "user", "the user prefers X")
    assert len(fake.writes) == 1
    assert fake.writes[0]["source_file"] == "hermes-memory"
    assert fake.writes[0]["section"] == "user"
    assert fake.writes[0]["content"] == "the user prefers X"


def test_on_memory_write_remove_and_replace_ignored(make_provider):
    provider, fake = make_provider()
    _init(provider)
    provider.on_memory_write("remove", "memory", "old fact")
    provider.on_memory_write("replace", "memory", "new fact")
    assert fake.writes == []


def test_on_memory_write_without_client_does_not_raise(make_provider):
    provider, _ = make_provider()
    provider.on_memory_write("add", "user", "x")


# -- config -----------------------------------------------------------------

def test_get_config_schema_fields(make_provider):
    provider, _ = make_provider()
    fields = {f["key"] for f in provider.get_config_schema()}
    assert {"transport", "url", "binary", "project_id", "search_limit",
            "min_score", "scope"} <= fields
    assert all(not f.get("secret") for f in provider.get_config_schema())


def test_save_config_writes_plugins_ai_raccoon(make_provider, tmp_path):
    provider, _ = make_provider()
    hermes_home = tmp_path / "home"
    hermes_home.mkdir()
    (hermes_home / "config.yaml").write_text("memory:\n  provider: holographic\n", encoding="utf-8")
    provider.save_config({"transport": "stdio", "project_id": "p"}, str(hermes_home))
    content = (hermes_home / "config.yaml").read_text(encoding="utf-8")
    assert "ai-raccoon:" in content
    assert "transport: stdio" in content
    # pre-existing section preserved
    assert "provider: holographic" in content
