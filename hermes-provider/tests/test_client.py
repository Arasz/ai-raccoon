"""Unit tests for the MCP client factory (spawn args composition)."""

from __future__ import annotations

import pytest


def test_create_client_stdio_includes_status_words_by_default(client_module):
    client = client_module.create_client({})
    assert isinstance(client, client_module.StdioClient)
    assert "--status-words" in client._args


def test_create_client_stdio_omits_status_words_when_disabled(client_module):
    client = client_module.create_client({"status_words": False})
    assert isinstance(client, client_module.StdioClient)
    assert "--status-words" not in client._args


def test_create_client_stdio_keeps_binary_args_before_flag(client_module):
    client = client_module.create_client({"binary_args": ["--data-root", "/tmp/bank"]})
    assert client._args == ["--data-root", "/tmp/bank", "--status-words"]


def test_create_client_http_ignores_status_words(client_module):
    client = client_module.create_client({"transport": "http", "url": "http://127.0.0.1:7721/mcp"})
    assert isinstance(client, client_module.HttpClient)
