"""Behavior tests for scripts/hermes-provider-setup.py (RED first).

Hermetic: HERMES_HOME points at a temp profile; a fake `hermes` shim on
PATH stands in for the CLI (records its calls); the probe uses the real
hermes venv python when HERMES_PYTHON is set and spawns the real
ai-raccoon binary against a TEMP --data-root bank, so the real
~/.ai-raccoon bank is never touched.
"""

from __future__ import annotations

import os
import pathlib
import pytest
import subprocess
import sys
import textwrap

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


@pytest.fixture
def fake_hermes(tmp_path, monkeypatch):
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    hermes = bin_dir / "hermes"
    hermes.write_text(FAKE_HERMES, encoding="utf-8")
    hermes.chmod(0o755)
    log = tmp_path / "hermes-calls.log"
    monkeypatch.setenv("FAKE_HERMES_LOG", str(log))
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


def test_install_copies_plugin_and_activates(temp_home, fake_hermes):
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


def test_rerun_is_idempotent(temp_home, fake_hermes):
    first = run_setup("--source", str(SOURCE_PLUGIN))
    second = run_setup("--source", str(SOURCE_PLUGIN))
    assert first.returncode == 0 and second.returncode == 0
    assert "provider: ai-raccoon" in (temp_home / "config.yaml").read_text(encoding="utf-8")
    calls = fake_hermes.read_text(encoding="utf-8").strip().splitlines()
    assert len(calls) == 2  # one config-set per run


def test_probe_spawns_isolated_server_and_passes(temp_home, fake_hermes):
    """With HERMES_PYTHON set, the probe really spawns the server on a temp bank."""
    result = run_setup("--source", str(SOURCE_PLUGIN), "--python", str(HERMES_VENV_PYTHON))
    assert result.returncode == 0, result.stderr
    assert "[probe] PASS" in result.stdout, result.stdout


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
