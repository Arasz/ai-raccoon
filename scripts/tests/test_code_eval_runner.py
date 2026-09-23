"""run_code_eval.py support pieces (plan §E3), RED-first:

- idle-timeout plumbing in retrieval_tuning.server.start_server /
  retrieval_tuning.scratch.scratch_server: an optional param that reaches the
  serve argv only when given, so every existing caller (no idle_timeout arg)
  is byte-for-byte unchanged.
- reuse-bank data-root prep deletes every 'watches' row from the copied bank
  (memory item: a bank copy inherits live watches and re-ingests real dirs
  mid-experiment unless the watches table is cleared).
- busy-process refusal: `ps` output showing a competing ai-raccoon serve /
  aspire / dotnet test process (other than the runner's own pid) refuses to
  start unless --allow-busy is passed.
"""

from __future__ import annotations

import importlib.util
import os
import sqlite3
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
RUNNER_PATH = REPO_ROOT / "scripts" / "retrieval_tuning" / "run_code_eval.py"
FIXTURE_DIR = Path(__file__).resolve().parent / "fixtures" / "code_eval"
sys.path.insert(0, str(REPO_ROOT / "scripts" / "src"))

from retrieval_tuning.server import start_server  # noqa: E402


def _load_runner():
    spec = importlib.util.spec_from_file_location("run_code_eval", RUNNER_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class TestIdleTimeoutReachesArgv:
    @pytest.fixture
    def fake_serve_binary(self, tmp_path):
        """Emulates `ai-raccoon serve --port 0`: records argv, writes the token, prints the URL."""
        script = tmp_path / "fake-serve.py"
        script.write_text(
            "#!/usr/bin/env python3\n"
            "import sys\n"
            "from pathlib import Path\n"
            "args = sys.argv[1:]\n"
            "root = Path(args[args.index('--data-root') + 1])\n"
            "(root / 'argv.txt').write_text(' '.join(args))\n"
            "(root / 'mcp-token').write_text('fake-token-123')\n"
            "print('ai-raccoon: serve listening on http://127.0.0.1:39852/mcp', flush=True)\n"
            "import time; time.sleep(120)\n"
        )
        os.chmod(script, 0o755)
        return script

    def test_no_idle_timeout_arg_omits_the_flag_entirely(self, tmp_path, fake_serve_binary):
        data_root = tmp_path / "scratch-bank"
        data_root.mkdir()
        server = start_server(data_root, binary=str(fake_serve_binary), skip_health_check=True)
        try:
            argv_text = (data_root / "argv.txt").read_text()
            assert "--idle-timeout" not in argv_text
        finally:
            server.stop()

    def test_idle_timeout_zero_reaches_argv(self, tmp_path, fake_serve_binary):
        data_root = tmp_path / "scratch-bank"
        data_root.mkdir()
        server = start_server(
            data_root, binary=str(fake_serve_binary), skip_health_check=True, idle_timeout="0"
        )
        try:
            argv_text = (data_root / "argv.txt").read_text()
            assert "--idle-timeout 0" in argv_text
        finally:
            server.stop()


class TestReuseBankDeletesWatches:
    def _make_bank_with_watches(self, path: Path) -> None:
        conn = sqlite3.connect(str(path))
        try:
            conn.execute(
                "CREATE TABLE watches (project_id TEXT NOT NULL, path TEXT NOT NULL, "
                "created_at INTEGER NOT NULL, last_change_ts INTEGER NOT NULL, "
                "PRIMARY KEY (project_id, path))"
            )
            conn.execute(
                "INSERT INTO watches VALUES ('ai-raccoon', '/Users/me/live-project', 1, 1)"
            )
            conn.execute("CREATE TABLE watch_files (project_id TEXT, path TEXT, file_hash TEXT)")
            conn.execute("INSERT INTO watch_files VALUES ('ai-raccoon', '/Users/me/live-project/a.py', 'x')")
            conn.commit()
        finally:
            conn.close()

    def test_reused_bank_copy_has_zero_watch_rows(self, tmp_path):
        runner = _load_runner()
        source_bank = tmp_path / "source-bank"
        source_bank.mkdir()
        self._make_bank_with_watches(source_bank / "memory.db")

        dest = tmp_path / "scratch-data-root"
        runner.prepare_reused_bank(source_bank, dest)

        conn = sqlite3.connect(f"file:{dest / 'memory.db'}?mode=ro", uri=True)
        try:
            count = conn.execute("SELECT count(*) FROM watches").fetchone()[0]
        finally:
            conn.close()
        assert count == 0

    def test_delete_watches_reports_rows_removed(self, tmp_path):
        runner = _load_runner()
        db_path = tmp_path / "memory.db"
        self._make_bank_with_watches(db_path)
        removed = runner.delete_watches(db_path)
        assert removed == 1


class TestBusyProcessRefusal:
    PS_HEADER = "  PID  %CPU COMMAND"
    PS_CLEAN = PS_HEADER + "\n" + "  100   1.0 /usr/bin/python3 some_script.py\n" + "  200   0.0 -zsh\n"
    PS_BUSY_SERVE = (
        PS_HEADER
        + "\n"
        + "  100   2.0 /usr/bin/python3 run_code_eval.py\n"
        + "  555  95.0 /usr/local/bin/ai-raccoon --data-root /home/me/.ai-raccoon serve --port 7721\n"
    )
    PS_BUSY_ASPIRE = PS_HEADER + "\n" + "  777  60.0 dotnet exec AppHost.Aspire.dll\n"
    PS_BUSY_DOTNET_TEST = PS_HEADER + "\n" + "  888 140.0 dotnet test src/AiRaccoon.sln\n"
    PS_IDLE_SERVE = (
        PS_HEADER
        + "\n"
        + "  556   0.3 /usr/local/bin/ai-raccoon --data-root /home/me/.ai-raccoon serve --port 7721\n"
    )

    def test_an_idle_competitor_is_not_busy(self):
        # The live MCP server and other sessions' idle processes are always present;
        # only one actually burning CPU competes with a drain.
        runner = _load_runner()
        assert runner.find_busy_processes(self.PS_IDLE_SERVE) == []

    def test_clean_ps_output_finds_nothing(self):
        runner = _load_runner()
        assert runner.find_busy_processes(self.PS_CLEAN) == []

    def test_observe_is_not_a_false_positive_for_serve(self):
        # Real ps output hit during the smoke run: a 'chatter observe' process whose
        # argv also carries the repo path '.../ai-raccoon' — "serve" as a raw
        # substring of "observe" must not trip the ai-raccoon-serve pattern.
        runner = _load_runner()
        ps_output = (
            self.PS_HEADER
            + "\n"
            + "  321  90.0 chatter observe --filter=ROLLOUT --repo=/Users/me/ai-raccoon --analytics=true\n"
        )
        assert runner.find_busy_processes(ps_output) == []

    def test_preserve_and_deserved_are_not_false_positives(self):
        runner = _load_runner()
        ps_output = (
            self.PS_HEADER
            + "\n"
            + "  322  90.0 some-tool --preserve-state --path ai-raccoon\n"
            + "  323  90.0 another-tool --well-deserved ai-raccoon\n"
        )
        assert runner.find_busy_processes(ps_output) == []

    def test_ai_raccoon_serve_is_flagged(self):
        runner = _load_runner()
        found = runner.find_busy_processes(self.PS_BUSY_SERVE)
        assert len(found) == 1
        assert found[0]["pid"] == 555
        assert found[0]["label"] == "ai-raccoon serve"

    def test_aspire_inside_a_longer_token_is_not_busy(self):
        runner = _load_runner()
        ps_output = self.PS_HEADER + "\n" + "  324  90.0 unaspired-widget --run\n"
        assert runner.find_busy_processes(ps_output) == []

    def test_aspire_is_flagged(self):
        runner = _load_runner()
        found = runner.find_busy_processes(self.PS_BUSY_ASPIRE)
        assert len(found) == 1
        assert found[0]["label"] == "aspire"

    def test_dotnet_test_is_flagged(self):
        runner = _load_runner()
        found = runner.find_busy_processes(self.PS_BUSY_DOTNET_TEST)
        assert len(found) == 1
        assert found[0]["label"] == "dotnet test"

    def test_own_pid_is_excluded(self):
        runner = _load_runner()
        found = runner.find_busy_processes(self.PS_BUSY_SERVE, exclude_pids={555})
        assert found == []

    def test_check_not_busy_raises_when_a_competitor_is_running(self):
        runner = _load_runner()
        with pytest.raises(runner.BusyProcessError):
            runner.check_not_busy(allow_busy=False, own_pid=1, ps_fn=lambda: self.PS_BUSY_SERVE)

    def test_check_not_busy_allows_override(self):
        runner = _load_runner()
        # Must not raise.
        runner.check_not_busy(allow_busy=True, own_pid=1, ps_fn=lambda: self.PS_BUSY_SERVE)

    def test_check_not_busy_passes_on_clean_ps_output(self):
        runner = _load_runner()
        runner.check_not_busy(allow_busy=False, own_pid=1, ps_fn=lambda: self.PS_CLEAN)


class TestFixtureCorpus:
    """The tiny fixture corpus this lane ships under scripts/tests/fixtures/code_eval/."""

    def test_queries_file_has_four_to_six_entries_with_one_negative(self):
        runner = _load_runner()
        queries = runner.load_queries(FIXTURE_DIR / "queries.json")
        assert 4 <= len(queries) <= 6
        assert sum(1 for q in queries if q.get("negativeTest")) == 1
        ids = [q["id"] for q in queries]
        assert len(ids) == len(set(ids))

    def test_every_non_negative_query_targets_a_real_fixture_file(self):
        runner = _load_runner()
        queries = runner.load_queries(FIXTURE_DIR / "queries.json")
        for entry in queries:
            if entry.get("negativeTest"):
                continue
            repo_relative = entry["expectedSource"].split(":", 1)[1]
            assert (FIXTURE_DIR / "corpus" / repo_relative).is_file(), entry["id"]

    def test_manifest_join_resolves_every_fixture_file(self):
        runner = _load_runner()
        manifest_rows = runner.load_manifest(FIXTURE_DIR / "MANIFEST.json")
        for source_file in (FIXTURE_DIR / "corpus").glob("*"):
            if not source_file.is_file():
                continue  # e.g. a stray __pycache__ dir from importing a fixture .py file
            absolute_path = str(source_file)
            row = runner.match_manifest_row(absolute_path, manifest_rows)
            assert row is not None, absolute_path
            assert row["language"] in {"python", "csharp", "javascript"}

    def test_chunk_counts_by_language_band_over_a_fake_code_entries_table(self, tmp_path):
        runner = _load_runner()
        manifest_rows = runner.load_manifest(FIXTURE_DIR / "MANIFEST.json")
        db_path = tmp_path / "memory.db"
        conn = sqlite3.connect(str(db_path))
        try:
            conn.execute("CREATE TABLE code_entries (path TEXT NOT NULL)")
            conn.executemany(
                "INSERT INTO code_entries (path) VALUES (?)",
                [
                    (str(FIXTURE_DIR / "corpus" / "greeter.py"),),
                    (str(FIXTURE_DIR / "corpus" / "test_greeter.py"),),
                    (str(FIXTURE_DIR / "corpus" / "IntervalOverlap.cs"),),
                ],
            )
            conn.commit()
        finally:
            conn.close()
        counts = runner.chunk_counts_by_language_band(db_path, manifest_rows)
        assert counts["python:small"] == 2
        assert counts["csharp:small"] == 1


class TestHitRecord:
    """--save-hits keeps each query's raw ranked hits so an offline re-rank can re-score them."""

    def test_keeps_rank_order_and_the_fields_scoring_reads(self):
        runner = _load_runner()
        results = [
            {"hash": "h1", "ranking": 1.0, "path": "/c/gin/tree.go", "lineStart": 3, "lineEnd": 40, "snippet": "x"},
            {"hash": "h2", "ranking": 0.7, "path": "/c/gin/tree_test.go", "lineStart": 1, "lineEnd": 9, "snippet": "y"},
        ]

        record = runner.hit_record({"id": "gin-001"}, results)

        assert record == {
            "id": "gin-001",
            "hits": [
                {"hash": "h1", "ranking": 1.0, "path": "/c/gin/tree.go", "lineStart": 3, "lineEnd": 40},
                {"hash": "h2", "ranking": 0.7, "path": "/c/gin/tree_test.go", "lineStart": 1, "lineEnd": 9},
            ],
        }


class TestDrainFlag:
    def test_drain_defaults_off_and_can_be_requested_for_a_reused_bank(self):
        runner = _load_runner()
        base = ["--binary", "b", "--corpus-root", "c", "--queries", "q", "--arm", "a", "--reuse-bank", "r"]

        assert runner.parse_args(base).drain is False
        assert runner.parse_args(base + ["--drain"]).drain is True
