"""Unit tests for the MCP client: spawn args composition and envelope handling."""

from __future__ import annotations

import concurrent.futures

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


# -- token_file / _auth_headers (loopback token, D1-D3 + R3) ----------------


def test_create_client_http_passes_token_file_through(client_module, tmp_path):
    """N5: create_client must forward token_file, or it can never reach the client."""
    token_file = tmp_path / "mcp-token"
    token_file.write_text("abc123\n")
    client = client_module.create_client({"transport": "http", "token_file": str(token_file)})
    assert client._auth_headers() == {"X-AiRaccoon-Token": "abc123"}


def test_auth_headers_trims_trailing_newline(client_module, tmp_path):
    token_file = tmp_path / "mcp-token"
    token_file.write_text("  abc123  \n")
    client = client_module.HttpClient(token_file=str(token_file))
    assert client._auth_headers() == {"X-AiRaccoon-Token": "abc123"}


def test_auth_headers_missing_file_returns_no_header(client_module, tmp_path):
    client = client_module.HttpClient(token_file=str(tmp_path / "does-not-exist"))
    assert client._auth_headers() == {}


def test_auth_headers_empty_file_returns_no_header(client_module, tmp_path):
    token_file = tmp_path / "mcp-token"
    token_file.write_text("")
    client = client_module.HttpClient(token_file=str(token_file))
    assert client._auth_headers() == {}


def test_auth_headers_whitespace_only_file_returns_no_header(client_module, tmp_path):
    token_file = tmp_path / "mcp-token"
    token_file.write_text("   \n\t  ")
    client = client_module.HttpClient(token_file=str(token_file))
    assert client._auth_headers() == {}


def test_auth_headers_unreadable_file_returns_no_header(client_module, tmp_path):
    token_file = tmp_path / "mcp-token"
    token_file.write_text("secret")
    token_file.chmod(0o000)
    try:
        client = client_module.HttpClient(token_file=str(token_file))
        assert client._auth_headers() == {}
    finally:
        token_file.chmod(0o600)  # restore so tmp_path teardown can remove it


def test_create_client_http_empty_token_file_disables_header(client_module, tmp_path):
    client = client_module.create_client({"transport": "http", "token_file": ""})
    assert client._auth_headers() == {}


def test_auth_headers_expands_tilde(client_module, tmp_path, monkeypatch):
    monkeypatch.setenv("HOME", str(tmp_path))
    token_file = tmp_path / "mcp-token"
    token_file.write_text("tilde-token")
    client = client_module.HttpClient(token_file="~/mcp-token")
    assert client._auth_headers() == {"X-AiRaccoon-Token": "tilde-token"}


def test_auth_headers_attaches_for_127_0_0_1(client_module, tmp_path):
    token_file = tmp_path / "mcp-token"
    token_file.write_text("secret")
    client = client_module.HttpClient(url="http://127.0.0.1:7721/mcp", token_file=str(token_file))
    assert client._auth_headers() == {"X-AiRaccoon-Token": "secret"}


def test_auth_headers_attaches_for_localhost(client_module, tmp_path):
    token_file = tmp_path / "mcp-token"
    token_file.write_text("secret")
    client = client_module.HttpClient(url="http://localhost:7721/mcp", token_file=str(token_file))
    assert client._auth_headers() == {"X-AiRaccoon-Token": "secret"}


def test_auth_headers_attaches_for_ipv6_loopback(client_module, tmp_path):
    token_file = tmp_path / "mcp-token"
    token_file.write_text("secret")
    client = client_module.HttpClient(url="http://[::1]:7721/mcp", token_file=str(token_file))
    assert client._auth_headers() == {"X-AiRaccoon-Token": "secret"}


def test_auth_headers_withheld_for_a_remote_host(client_module, tmp_path):
    """R3: url is arbitrary and passed straight through — an unconditional header would ship
    the loopback secret in cleartext to whatever host a user configures."""
    token_file = tmp_path / "mcp-token"
    token_file.write_text("secret")
    client = client_module.HttpClient(url="http://example.com:7721/mcp", token_file=str(token_file))
    assert client._auth_headers() == {}


def test_get_config_schema_documents_token_file(provider_module):
    provider = provider_module.AiRaccoonMemoryProvider(config={})
    schema = provider.get_config_schema()
    entry = next(e for e in schema if e["key"] == "token_file")
    assert entry["default"] == "~/.ai-raccoon/mcp-token"
    description = entry["description"].lower()
    assert "read at connect" in description or "connect" in description
    assert "never" in description and "config.yaml" in description


def test_connect_failure_names_the_url_and_the_token_file(client_module, tmp_path):
    """A failed connect must say what it tried. Measured before this test existed: a gated
    server plus a missing token file surfaced `CancelledError` with an empty message, so the
    provider logged `connect failed:` and nothing else — the same unreadable failure this
    whole change exists to remove."""

    class Boom(client_module.HttpClient):
        async def _open(self):
            raise RuntimeError("backend said no")

    client = Boom(url="http://127.0.0.1:7799/mcp", token_file=str(tmp_path / "absent-token"))
    with pytest.raises(client_module.AiRaccoonError) as caught:
        client.connect()
    message = str(caught.value)
    assert "http://127.0.0.1:7799/mcp" in message
    assert "absent-token" in message
    assert "backend said no" in message


def test_connect_failure_with_an_empty_reason_still_names_the_transport(client_module, tmp_path):
    """The exact observed shape: an exception whose str() is empty must not produce an
    error message that is also empty."""

    class Silent(client_module.HttpClient):
        async def _open(self):
            raise concurrent.futures.CancelledError()

    client = Silent(url="http://127.0.0.1:7799/mcp", token_file=str(tmp_path / "absent-token"))
    with pytest.raises(client_module.AiRaccoonError) as caught:
        client.connect()
    message = str(caught.value)
    assert "CancelledError" in message
    assert "http://127.0.0.1:7799/mcp" in message
