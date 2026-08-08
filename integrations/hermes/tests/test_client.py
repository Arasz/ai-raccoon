"""Unit tests for the MCP client: spawn args composition and envelope handling."""

from __future__ import annotations

import pytest


class _FakeContent:
    type = "text"

    def __init__(self, text: str):
        self.text = text


class _FakeResult:
    """The shape of an mcp CallToolResult, as far as _text cares."""

    isError = False

    def __init__(self, text: str):
        self.content = [_FakeContent(text)]


def test_create_client_stdio_includes_quiet_by_default(client_module):
    client = client_module.create_client({})
    assert isinstance(client, client_module.StdioClient)
    assert "--quiet" in client._args


def test_create_client_stdio_omits_quiet_when_disabled(client_module):
    client = client_module.create_client({"quiet": False})
    assert isinstance(client, client_module.StdioClient)
    assert "--quiet" not in client._args


def test_create_client_stdio_keeps_binary_args_before_flag(client_module):
    client = client_module.create_client({"binary_args": ["--data-root", "/tmp/bank"]})
    assert client._args == ["--data-root", "/tmp/bank", "--quiet"]


def test_create_client_http_ignores_quiet(client_module):
    client = client_module.create_client({"transport": "http", "url": "http://127.0.0.1:7721/mcp"})
    assert isinstance(client, client_module.HttpClient)


def test_unwrap_strips_the_server_response_envelope(client_module):
    """Every tool answers ApiEnvelope<T>: the payload is under 'data'."""
    result = _FakeResult(
        '{"data": {"results": [{"hash": "h1"}]}, "meta": {"waitingPromotionsCount": 0}}')
    assert client_module._unwrap(client_module._text(result)) == {"results": [{"hash": "h1"}]}


def test_unwrap_passes_through_a_payload_that_is_not_an_envelope(client_module):
    assert client_module._unwrap({"results": []}) == {"results": []}


def test_unwrap_needs_both_envelope_keys(client_module):
    """A payload whose own shape has a 'data' field is not an envelope."""
    assert client_module._unwrap({"data": 1}) == {"data": 1}


def test_unwrap_leaves_non_mapping_payloads_alone(client_module):
    assert client_module._unwrap("plain text") == "plain text"
