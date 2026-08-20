"""Behavior tests for scripts/hermes-provider-setup.py (RED first).

Hermetic: HERMES_HOME points at a temp profile; fake `hermes` and
`ai-raccoon` shims on PATH stand in for the CLIs (each records its
calls), so full-script runs never touch the real ~/.ai-raccoon bank.
The probe test keeps the REAL ai-raccoon binary and the hermes venv
python (HERMES_PYTHON): its probe spawns the server against a TEMP
--data-root bank, and the script's real `settings extract exclude add
hermes/` write is a deduped no-op while `hermes/` is already excluded.
"""

from __future__ import annotations

import os
import pathlib
import shutil
import socket
import subprocess
import sys
import textwrap
import time

import pytest

REPO = pathlib.Path(__file__).resolve().parents[3]
SCRIPT = REPO / "scripts" / "hermes-provider-setup.py"
SOURCE_PLUGIN = REPO / "integrations" / "hermes" / "ai-raccoon"
HERMES_VENV_PYTHON = pathlib.Path("/Users/arasz/.hermes/hermes-agent/venv/bin/python")

FAKE_HERMES = textwrap.dedent("""\
    #!/usr/bin/env python3
    import os, sys, yaml
    from pathlib import Path

    log = Path(os.environ["FAKE_HERMES_LOG"])
    with open(log, "a", encoding="utf-8") as f:
        f.write(" ".join(sys.argv[1:]) + "\\n")

    if len(sys.argv) == 5 and sys.argv[1] == "config" and sys.argv[2] == "set" \\
            and sys.argv[3] == "memory.provider":
        home = Path(os.environ.get("HERMES_HOME", Path.home() / ".hermes"))
        cfg_path = home / "config.yaml"
        cfg = yaml.safe_load(cfg_path.read_text(encoding="utf-8")) or {}
        cfg.setdefault("memory", {})["provider"] = sys.argv[4]
        cfg_path.write_text(yaml.safe_dump(cfg, default_flow_style=False), encoding="utf-8")
        print(f"OK set memory.provider = {sys.argv[4]}")
    elif len(sys.argv) == 2 and sys.argv[1] == "config":
        print("config: fake hermes (test shim)")
    else:
        print("fake hermes: unsupported", sys.argv[1:], file=sys.stderr)
        sys.exit(1)
""")


FAKE_AIRACCOON = textwrap.dedent("""\
    #!/usr/bin/env python3
    import os, sys

    log = os.environ["FAKE_AIRACCOON_LOG"]
    with open(log, "a", encoding="utf-8") as f:
        f.write(" ".join(sys.argv[1:]) + "\\n")

    if os.environ.get("FAKE_AIRACCOON_FAIL") == "1":
        print("boom", file=sys.stderr)
        sys.exit(3)
    sys.exit(0)
""")


@pytest.fixture
def fake_hermes(tmp_path, monkeypatch):
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    hermes = bin_dir / "hermes"
    hermes.write_text(
        FAKE_HERMES.replace("#!/usr/bin/env python3", f"#!{sys.executable}"),
        encoding="utf-8")
    hermes.chmod(0o755)
    log = tmp_path / "hermes-calls.log"
    monkeypatch.setenv("FAKE_HERMES_LOG", str(log))
    monkeypatch.setenv("PATH", str(bin_dir) + os.pathsep + os.environ["PATH"])
    return log


@pytest.fixture
def fake_ai_raccoon(tmp_path, monkeypatch):
    """Shim for the ai-raccoon CLI: records argv to a log, exits 0 (or exits 3
    with 'boom' on stderr when FAKE_AIRACCOON_FAIL=1)."""
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir(exist_ok=True)
    ai_raccoon = bin_dir / "ai-raccoon"
    ai_raccoon.write_text(
        FAKE_AIRACCOON.replace("#!/usr/bin/env python3", f"#!{sys.executable}"),
        encoding="utf-8")
    ai_raccoon.chmod(0o755)
    log = tmp_path / "ai-raccoon-calls.log"
    monkeypatch.setenv("FAKE_AIRACCOON_LOG", str(log))
    monkeypatch.setenv("PATH", str(bin_dir) + os.pathsep + os.environ["PATH"])
    return log


@pytest.fixture
def temp_home(tmp_path, monkeypatch):
    home = tmp_path / "home"
    home.mkdir()
    (home / "config.yaml").write_text(
        "memory:\n  provider: holographic\n", encoding="utf-8")
    monkeypatch.setenv("HERMES_HOME", str(home))
    return home


def run_setup(*args, env=None):
    full_env = dict(os.environ)
    if env:
        full_env.update(env)
    return subprocess.run(
        [sys.executable, str(SCRIPT), *args],
        capture_output=True, text=True, env=full_env, timeout=120)


def test_check_reports_not_installed_without_changing(temp_home, fake_hermes):
    result = run_setup("--check")
    assert result.returncode == 0, result.stderr
    assert "plugin installed: False" in result.stdout
    assert not (temp_home / "plugins" / "ai-raccoon").exists()
    assert "provider: holographic" in (temp_home / "config.yaml").read_text(encoding="utf-8")
    assert not fake_hermes.exists()  # no config set was issued (log never created)


def test_default_source_resolves_to_shipped_plugin(temp_home, fake_hermes):
    """Without --source the script must find the plugin that ships in this repo."""
    result = run_setup("--check")
    assert result.returncode == 0, result.stderr
    assert f"plugin source:    {SOURCE_PLUGIN}" in result.stdout, result.stdout


def test_install_copies_plugin_and_activates(temp_home, fake_hermes, fake_ai_raccoon):
    result = run_setup("--source", str(SOURCE_PLUGIN))
    assert result.returncode == 0, result.stderr
    installed = temp_home / "plugins" / "ai-raccoon"
    assert (installed / "__init__.py").exists()
    assert (installed / "client.py").exists()
    assert (installed / "plugin.yaml").exists()
    config = (temp_home / "config.yaml").read_text(encoding="utf-8")
    assert "provider: ai-raccoon" in config
    calls = fake_hermes.read_text(encoding="utf-8")
    assert "config set memory.provider ai-raccoon" in calls


def test_rerun_is_idempotent(temp_home, fake_hermes, fake_ai_raccoon):
    first = run_setup("--source", str(SOURCE_PLUGIN))
    second = run_setup("--source", str(SOURCE_PLUGIN))
    assert first.returncode == 0 and second.returncode == 0
    assert "provider: ai-raccoon" in (temp_home / "config.yaml").read_text(encoding="utf-8")
    calls = fake_hermes.read_text(encoding="utf-8").strip().splitlines()
    assert len(calls) == 2  # one config-set per run


def test_exclude_prefix_uses_settings_verb(temp_home, fake_hermes, fake_ai_raccoon):
    """Exclude write must use the settings verb family.

    The recorded-argv assertion is the RED discriminator: old code records
    'extract exclude add hermes/', new code 'settings extract exclude add
    hermes/'. The success-line assertion alone does not discriminate (both
    print it when the shim exits 0).
    """
    result = run_setup("--source", str(SOURCE_PLUGIN))
    assert result.returncode == 0, result.stderr
    calls = fake_ai_raccoon.read_text(encoding="utf-8")
    assert "settings extract exclude add hermes/" in calls, calls
    assert "[exclude] source prefix 'hermes/' excluded from shared extraction" in result.stdout, result.stdout


def test_exclude_failure_path_mentions_settings_verb(temp_home, fake_hermes, fake_ai_raccoon, monkeypatch):
    """The fallback WARNING must show the operator the settings verb."""
    monkeypatch.setenv("FAKE_AIRACCOON_FAIL", "1")
    result = run_setup("--source", str(SOURCE_PLUGIN))
    assert result.returncode == 0, result.stderr
    assert "settings extract exclude add" in result.stdout, result.stdout


def _port_listening(host: str = "127.0.0.1", port: int = 7721) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(0.5)
        return s.connect_ex((host, port)) == 0


def test_probe_spawns_isolated_server_and_passes(temp_home, fake_hermes, tmp_path):
    """With HERMES_PYTHON set, the probe really spawns the server on a temp bank.

    Foreign-server precondition (M1-M3): 127.0.0.1:7721 must be owned by SOME
    server before the script runs — the real ai-raccoon server, a throwaway we
    spawn, or a foreign listener. With the port free, old proxy-transport code
    starts its own backend and the test would go green without pinning the
    stdio fix. If no listener can be established at all, fail instead.
    """
    spawned = None
    foreign_bank = tmp_path / "foreign-bank"
    try:
        if not _port_listening():
            binary = shutil.which("ai-raccoon")
            assert binary, "ai-raccoon not found on PATH; cannot establish the foreign-server precondition"
            # M1: --data-root is a root option and must precede the verb;
            # 'serve --port 7721 --data-root <dir>' exits 15 and binds nothing.
            spawned = subprocess.Popen(
                [binary, "--data-root", str(foreign_bank), "serve", "--port", "7721"],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            deadline = time.monotonic() + 10.0
            while time.monotonic() < deadline and not _port_listening():
                time.sleep(0.25)
            if not _port_listening():
                # M2: classify by liveness, not spawn outcome — `serve` ATTACHES
                # and exits 0 when an ai-raccoon server already owns the port,
                # exits non-zero on a foreign listener, and stays alive when it
                # bound the port itself. All are fine; a missing listener is not.
                rc = spawned.poll()
                state = "still running" if rc is None else f"exited with code {rc}"
                pytest.fail(
                    f"no listener on 127.0.0.1:7721 after 10s (throwaway {state}); "
                    "cannot establish the foreign-server precondition — with the "
                    "port free, old proxy code would start its own backend and pass")
        result = run_setup("--source", str(SOURCE_PLUGIN), "--python", str(HERMES_VENV_PYTHON))
        assert result.returncode == 0, result.stderr
        assert "[probe] PASS" in result.stdout, result.stdout
    finally:
        # M3: teardown — only when the test bound the port itself.
        if spawned is not None and spawned.poll() is None:
            spawned.terminate()
            try:
                spawned.wait(timeout=10)
            except subprocess.TimeoutExpired:
                spawned.kill()
                spawned.wait()
        if foreign_bank.exists():
            shutil.rmtree(foreign_bank, ignore_errors=True)


def test_missing_source_fails(temp_home, fake_hermes):
    result = run_setup("--source", "/nonexistent/plugin-dir")
    assert result.returncode != 0
    assert "source" in result.stderr.lower() or "source" in result.stdout.lower()


def test_activation_failure_is_fatal(temp_home, fake_hermes, monkeypatch):
    # break the fake hermes so config set fails (it lives in bin/)
    broken = fake_hermes.parent / "bin" / "hermes"
    broken.write_text("#!/usr/bin/env python3\nimport sys\nprint('boom', file=sys.stderr)\nsys.exit(3)\n",
                      encoding="utf-8")
    broken.chmod(0o755)
    result = run_setup("--source", str(SOURCE_PLUGIN))
    assert result.returncode != 0
    assert "boom" in result.stderr
