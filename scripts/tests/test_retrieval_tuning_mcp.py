"""Unit tests for retrieval_tuning.mcp — streamable-HTTP JSON-RPC client against a stub server."""

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import pytest

from retrieval_tuning.mcp import MCPClient, McpError, parse_sse_body, read_token


class StubMcpServer:
    """A minimal streamable-HTTP MCP endpoint that answers from a canned response queue."""

    def __init__(self, responses):
        self.responses = list(responses)
        self.requests = []

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                length = int(self.headers.get("Content-Length", 0))
                body = self.rfile.read(length)
                self.server.requests.append(
                    {
                        "path": self.path,
                        "headers": dict(self.headers),
                        "body": json.loads(body),
                    }
                )
                if not self.server.responses:
                    self.send_response(500)
                    self.end_headers()
                    return
                status, content_type, payload = self.server.responses.pop(0)
                self.send_response(status)
                self.send_header("Content-Type", content_type)
                self.end_headers()
                self.wfile.write(payload.encode())

            def log_message(self, *args):
                pass

        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.httpd.requests = self.requests
        self.httpd.responses = self.responses
        self.thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self.thread.start()

    @property
    def base_url(self):
        host, port = self.httpd.server_address
        return f"http://127.0.0.1:{port}"

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()


def sse_frame(payload: dict) -> str:
    return f"event: message\ndata: {json.dumps(payload)}\n\n"


def rpc_result(req_id, result) -> dict:
    return {"jsonrpc": "2.0", "id": req_id, "result": result}


def tools_call_result(req_id, text: str) -> dict:
    return rpc_result(req_id, {"content": [{"type": "text", "text": text}]})


def make_client(stub, token="tok-123", base=None):
    return MCPClient(base or stub.base_url, token=token)


class TestParseSseBody:
    def test_event_message_then_data(self):
        assert parse_sse_body('event: message\ndata: {"a": 1}\n\n') == {"a": 1}

    def test_multiple_data_frames_use_the_last(self):
        text = 'event: message\ndata: {"a": 1}\n\nevent: message\ndata: {"a": 2}\n\n'
        assert parse_sse_body(text) == {"a": 2}

    def test_plain_json_fallback(self):
        assert parse_sse_body('{"b": 2}') == {"b": 2}


class TestInitialize:
    def test_handshake_returns_server_info(self):
        stub = StubMcpServer(
            [
                (200, "application/json", sse_frame(rpc_result(1, {"protocolVersion": "2025-06-18", "serverInfo": {"name": "ai-raccoon", "version": "1.2.3"}}))),
                (202, "text/plain", ""),  # notifications/initialized
            ]
        )
        try:
            client = make_client(stub)
            info = client.initialize()
            assert info["serverInfo"]["name"] == "ai-raccoon"
            assert len(stub.requests) == 2  # initialize + notifications/initialized
            first = stub.requests[0]["body"]
            assert first["method"] == "initialize"
            assert first["params"]["protocolVersion"] == "2025-06-18"
            assert stub.requests[1]["body"]["method"] == "notifications/initialized"
        finally:
            stub.close()

    def test_accepts_base_url_with_or_without_mcp_suffix(self):
        stub = StubMcpServer(
            [
                (200, "application/json", sse_frame(rpc_result(1, {}))),
                (202, "text/plain", ""),
            ]
        )
        try:
            # A full endpoint URL ('.../mcp') must not produce a '/mcp/mcp' POST.
            make_client(stub, base=f"{stub.base_url}/mcp").initialize()
            assert stub.requests[0]["path"] == "/mcp"
        finally:
            stub.close()

    def test_bearer_token_is_sent(self):
        stub = StubMcpServer(
            [
                (200, "application/json", sse_frame(rpc_result(1, {}))),
                (202, "text/plain", ""),
            ]
        )
        try:
            make_client(stub).initialize()
            auth = stub.requests[0]["headers"].get("Authorization")
            assert auth == "Bearer tok-123"
        finally:
            stub.close()


class TestMemorySearch:
    def test_unwraps_text_content_into_results(self):
        envelope = json.dumps({"results": [{"hash": "abc123", "ranking": 1.0, "sourceFile": "docs/adr/0042-widget.md"}]})
        stub = StubMcpServer(
            [
                (200, "application/json", sse_frame(rpc_result(1, {}))),  # initialize
                (200, "application/json", sse_frame(rpc_result(2, {}))),  # initialized notif
                (200, "application/json", sse_frame(tools_call_result(3, envelope))),
            ]
        )
        try:
            client = make_client(stub)
            client.initialize()
            results = client.memory_search(
                project_id="ai-raccoon", query="hello", scope="project", limit=5, min_relative_score=0.0
            )
            assert results == [{"hash": "abc123", "ranking": 1.0, "sourceFile": "docs/adr/0042-widget.md"}]
            call = stub.requests[2]["body"]
            assert call["method"] == "tools/call"
            assert call["params"]["name"] == "memory_search"
            args = call["params"]["arguments"]
            assert args == {
                "projectId": "ai-raccoon",
                "query": "hello",
                "scope": "project",
                "limit": 5,
                "minRelativeScore": 0.0,
            }
            assert "rrfK" not in args  # settings-driven: no tuning args on the wire
        finally:
            stub.close()


class TestToolsList:
    def test_returns_tool_names(self):
        stub = StubMcpServer(
            [
                (200, "application/json", sse_frame(rpc_result(1, {}))),
                (200, "application/json", sse_frame(rpc_result(2, {}))),
                (
                    200,
                    "application/json",
                    sse_frame(
                        rpc_result(
                            3,
                            {"tools": [{"name": "memory_search"}, {"name": "memory_stats"}]},
                        )
                    ),
                ),
            ]
        )
        try:
            client = make_client(stub)
            client.initialize()
            tools = client.tools_list()
            assert [t["name"] for t in tools] == ["memory_search", "memory_stats"]
        finally:
            stub.close()


class TestErrors:
    def test_jsonrpc_error_raises_with_message(self):
        stub = StubMcpServer(
            [
                (
                    200,
                    "application/json",
                    sse_frame(
                        {
                            "jsonrpc": "2.0",
                            "id": 1,
                            "error": {"code": -32602, "message": "invalid-params: bad scope"},
                        }
                    ),
                ),
            ]
        )
        try:
            with pytest.raises(McpError) as exc:
                make_client(stub).initialize()
            assert "invalid-params: bad scope" in str(exc.value)
        finally:
            stub.close()

    def test_http_error_raises(self):
        stub = StubMcpServer([(401, "text/plain", "unauthorized")])
        try:
            with pytest.raises(McpError):
                make_client(stub).initialize()
        finally:
            stub.close()


class TestReadToken:
    def test_reads_token_file(self, tmp_path):
        token_file = tmp_path / "mcp-token"
        token_file.write_text("sekrit-token\n")
        assert read_token(tmp_path) == "sekrit-token"

    def test_missing_token_file_raises(self, tmp_path):
        with pytest.raises(FileNotFoundError):
            read_token(tmp_path / "empty")
