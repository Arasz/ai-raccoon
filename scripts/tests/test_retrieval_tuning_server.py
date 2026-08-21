"""Unit tests for retrieval_tuning.server — port parsing, safety asserts, lifecycle."""

import os
import time

import pytest

from retrieval_tuning.server import (
    LIVE_DATA_ROOT,
    PortNotFoundError,
    SafetyViolation,
    ServerStartError,
    ScratchServer,
    assert_port_not_7721,
    assert_safe_data_root,
    parse_bound_port,
    start_server,
)


class TestParseBoundPort:
    def test_serve_url_line(self):
        log = "info: ai-raccoon: serve listening on http://127.0.0.1:39481/mcp\n"
        assert parse_bound_port(log) == 39481

    def test_kestrel_now_listening_line(self):
        log = "Now listening on: http://127.0.0.1:39481\n"
        assert parse_bound_port(log) == 39481

    def test_plain_url_line(self):
        log = "http://127.0.0.1:39481/mcp\n"
        assert parse_bound_port(log) == 39481

    def test_last_bound_url_wins(self):
        log = (
            "Now listening on: http://127.0.0.1:39481\n"
            "Now listening on: http://127.0.0.1:39482\n"
        )
        assert parse_bound_port(log) == 39482

    def test_not_listening_yet_raises(self):
        with pytest.raises(PortNotFoundError):
            parse_bound_port("ai-raccoon: starting the backend...\n")

    def test_empty_log_raises(self):
        with pytest.raises(PortNotFoundError):
            parse_bound_port("")


class TestPortSafety:
    def test_7721_refused(self):
        with pytest.raises(SafetyViolation):
            assert_port_not_7721(7721)

    def test_other_ports_accepted(self):
        assert_port_not_7721(39481)
        assert_port_not_7721(0)


class TestDataRootSafety:
    def test_live_root_refused(self):
        with pytest.raises(SafetyViolation):
            assert_safe_data_root(LIVE_DATA_ROOT)

    def test_live_memory_db_refused(self):
        with pytest.raises(SafetyViolation):
            assert_safe_data_root(LIVE_DATA_ROOT / "memory.db")

    def test_subdir_of_live_root_refused(self):
        with pytest.raises(SafetyViolation):
            assert_safe_data_root(LIVE_DATA_ROOT / "some-subdir")

    def test_ancestor_of_live_root_refused(self):
        with pytest.raises(SafetyViolation):
            assert_safe_data_root(LIVE_DATA_ROOT.parent)

    def test_scratch_root_accepted(self, tmp_path):
        assert_safe_data_root(tmp_path)
        assert_safe_data_root(str(tmp_path))  # str form works too


class TestStartServerWithFakeBinary:
    @pytest.fixture
    def fake_serve_binary(self, tmp_path):
        """Emulates `ai-raccoon serve --port 0`: writes the token, prints the bound URL, sleeps."""
        script = tmp_path / "fake-serve.py"
        script.write_text(
            "#!/usr/bin/env python3\n"
            "import sys, time\n"
            "from pathlib import Path\n"
            "args = sys.argv[1:]\n"
            "root = Path(args[args.index('--data-root') + 1])\n"
            "(root / 'mcp-token').write_text('fake-token-123')\n"
            "print('ai-raccoon: serve listening on http://127.0.0.1:39851/mcp', flush=True)\n"
            "time.sleep(120)\n"
        )
        os.chmod(script, 0o755)
        return script

    def test_start_and_stop(self, tmp_path, fake_serve_binary):
        data_root = tmp_path / "scratch-bank"
        data_root.mkdir()
        server = start_server(data_root, binary=str(fake_serve_binary), skip_health_check=True)
        try:
            assert server.port == 39851
            assert server.port != 7721
            assert server.token == "fake-token-123"
            assert server.base_url == "http://127.0.0.1:39851/mcp"
        finally:
            server.stop()
        assert server.proc.poll() is not None  # SIGTERM + wait took it down

    def test_refuses_live_root_before_spawn(self, tmp_path, fake_serve_binary):
        with pytest.raises(SafetyViolation):
            start_server(LIVE_DATA_ROOT, binary=str(fake_serve_binary), skip_health_check=True)

    def test_start_failure_reports_log(self, tmp_path):
        script = tmp_path / "fail-serve.py"
        script.write_text(
            "#!/usr/bin/env python3\n"
            "import sys\n"
            "print('boom: cannot open bank', file=sys.stderr)\n"
            "sys.exit(3)\n"
        )
        os.chmod(script, 0o755)
        data_root = tmp_path / "scratch-bank"
        data_root.mkdir()
        with pytest.raises(ServerStartError) as exc:
            start_server(data_root, binary=str(script), skip_health_check=True)
        assert "boom" in str(exc.value)

    def test_search_builds_settings_driven_args(self):
        """ScratchServer.search must pass exactly the non-tuning args (settings-driven)."""

        class FakeClient:
            def __init__(self):
                self.kwargs = None

            def memory_search(self, **kwargs):
                self.kwargs = kwargs
                return [{"hash": "abc"}]

        client = FakeClient()
        server = ScratchServer(
            data_root="/tmp/scratch",
            port=39851,
            token="t",
            proc=None,
            log_path="/tmp/scratch/serve.log",
            binary="ai-raccoon",
            client=client,
        )
        results = server.search(
            {
                "id": "E1",
                "query": "hello",
                "targetProjectId": "ai-raccoon",
                "targetScope": "project",
                "searchLimit": 3,
            }
        )
        assert results == [{"hash": "abc"}]
        assert client.kwargs == {
            "project_id": "ai-raccoon",
            "query": "hello",
            "scope": "project",
            "limit": 3,
            "min_relative_score": 0.0,
        }
        assert "rrfK" not in client.kwargs  # tuning is settings-driven, never per-call
