"""Unit tests for the MCP client factory (spawn args composition)."""

from __future__ import annotations

import pytest


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
