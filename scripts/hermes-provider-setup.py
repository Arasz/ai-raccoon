#!/usr/bin/env python3
"""First-time setup for the ai-raccoon Hermes memory provider plugin.

Installs ``integrations/hermes/ai-raccoon`` into ``$HERMES_HOME/plugins/``,
probes it through Hermes' memory-plugin discovery (spawning a real
server against a TEMP ``--data-root`` bank, so the probe never touches
the real bank), activates it with ``hermes config set memory.provider
ai-raccoon``, and excludes the ``hermes/`` source prefix from shared
extraction (``ai-raccoon extract exclude add hermes/`` — a settings
write against the default bank, which is created on first open if
absent).

Usage:
    python3 scripts/hermes-provider-setup.py [--source DIR] [--python PY] [--check]

Idempotent: re-running re-installs and re-activates the same value.
``--check`` reports the current state and changes nothing.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PLUGIN_NAME = "ai-raccoon"
PROBE_MARKER = "hermes-provider-setup-probe:"
# Operator config applied at setup time (config channel, not code): shared extraction
# never promotes hermes' own session-scratch rows (source_file 'hermes/<session>').
EXCLUDE_PREFIX = "hermes/"

PROBE_CODE = r"""
import sys
from plugins.memory import discover_memory_providers, load_memory_provider
name = "ai-raccoon"
found = [(n, a) for n, d, a in discover_memory_providers() if n == name]
print("PROBE discovered=%s" % found, flush=True)
if not found:
    print("PROBE FAIL: provider not discoverable", flush=True)
    sys.exit(2)
provider = load_memory_provider(name)
if provider is None:
    print("PROBE FAIL: provider failed to load", flush=True)
    sys.exit(2)
if not provider.is_available():
    print("PROBE SKIP: provider not available (ai-raccoon binary missing?)", flush=True)
    sys.exit(0)
provider.initialize("setup-probe", hermes_home=sys.argv[1], platform="cli",
                    agent_context="primary", agent_identity="probe",
                    agent_workspace="hermes")
if provider._client is None:
    print("PROBE FAIL: client did not connect", flush=True)
    sys.exit(2)
print("PROBE PASS: %s spawned %s for project %s" % (
    type(provider).__name__, type(provider._client).__name__, provider.project_id), flush=True)
provider.shutdown()
"""


def hermes_home() -> Path:
    return Path(os.environ.get("HERMES_HOME", str(Path.home() / ".hermes")))


def default_source() -> Path:
    return Path(__file__).resolve().parent.parent / "integrations" / "hermes" / PLUGIN_NAME


def read_provider_setting(home: Path) -> str:
    """Line-scan config.yaml for memory.provider (no yaml dependency)."""
    cfg = home / "config.yaml"
    if not cfg.exists():
        return "(no config.yaml)"
    in_memory = False
    for line in cfg.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if re.match(r"^memory:\s*$", stripped):
            in_memory = True
            continue
        if in_memory and stripped and not stripped.startswith("#"):
            if re.match(r"^\S", line):
                break  # left the memory block
            match = re.match(r"^provider:\s*(\S+)", stripped)
            if match:
                return match.group(1)
    return "(not set)"


def install(source: Path, plugins_dir: Path) -> None:
    plugins_dir.mkdir(parents=True, exist_ok=True)
    target = plugins_dir / PLUGIN_NAME
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)
    print(f"[install] {PLUGIN_NAME} -> {target}")


def resolve_hermes_python(override: str | None) -> str | None:
    if override:
        return override
    env = os.environ.get("HERMES_PYTHON")
    if env:
        return env
    shim = shutil.which("hermes")
    if shim:
        try:
            head = Path(shim).read_text(encoding="utf-8", errors="replace")[:2000]
            match = re.search(r'exec\s+"([^"]+)/bin/hermes"', head)
            if match:
                candidate = Path(match.group(1)) / "bin" / "python"
                if candidate.exists():
                    return str(candidate)
        except OSError:
            pass
    return None


def run_probe(home: Path, plugins_dir: Path, python: str) -> None:
    """Load the plugin through Hermes discovery in an isolated HERMES_HOME.

    The temp home's config points the spawned server at a temp --data-root,
    so the probe can never touch the real bank.
    """
    with tempfile.TemporaryDirectory(prefix="hermes-setup-probe-") as tmp:
        tmp = Path(tmp)
        iso_home = tmp / "home"
        iso_home.mkdir()
        (iso_home / "plugins").symlink_to(plugins_dir, target_is_directory=True)
        bank = tmp / "bank"
        (iso_home / "config.yaml").write_text(
            "plugins:\n  ai-raccoon:\n    transport: stdio\n"
            f"    binary_args: ['--data-root', '{bank}']\n",
            encoding="utf-8")
        env = {**os.environ, "HERMES_HOME": str(iso_home)}
        result = subprocess.run(
            [python, "-c", PROBE_CODE, str(iso_home)],
            capture_output=True, text=True, env=env, timeout=60)
        for line in result.stdout.splitlines():
            if line.startswith("PROBE"):
                print("[probe] " + line.removeprefix("PROBE "))
        if result.returncode not in (0,):
            print("[probe] WARNING: probe failed (exit %d): %s"
                  % (result.returncode, result.stderr.strip()[-300:]))


def activate(home: Path) -> None:
    hermes = shutil.which("hermes")
    if not hermes:
        raise RuntimeError(
            "hermes CLI not found on PATH; run manually: "
            "hermes config set memory.provider ai-raccoon")
    env = {**os.environ, "HERMES_HOME": str(home)}
    result = subprocess.run(
        [hermes, "config", "set", "memory.provider", PLUGIN_NAME],
        capture_output=True, text=True, env=env, timeout=60)
    if result.returncode != 0:
        raise RuntimeError(
            f"hermes config set failed ({result.returncode}): "
            f"{result.stderr.strip() or result.stdout.strip()}")
    print(f"[activate] memory.provider = {PLUGIN_NAME} ({home / 'config.yaml'})")


def wire_exclude_prefix() -> None:
    """Exclude 'hermes/' source_file prefixes from shared extraction (best-effort).

    Runs the config CLI against the default bank (~/.ai-raccoon); the bank is
    created on first open if it does not exist yet. Failure only warns: extraction
    is off by default and the operator can add the prefix later with
    'ai-raccoon extract exclude add hermes/'.
    """
    binary = shutil.which("ai-raccoon")
    if not binary:
        print(f"[exclude] WARNING: ai-raccoon CLI not found; run manually: "
              f"ai-raccoon extract exclude add {EXCLUDE_PREFIX}")
        return
    result = subprocess.run(
        ["ai-raccoon", "extract", "exclude", "add", EXCLUDE_PREFIX],
        capture_output=True, text=True, timeout=60)
    if result.returncode != 0:
        print(f"[exclude] WARNING: extract exclude add failed ({result.returncode}): "
              f"{result.stderr.strip() or result.stdout.strip()}")
        return
    print(f"[exclude] source prefix '{EXCLUDE_PREFIX}' excluded from shared extraction")


def check(home: Path, source: Path) -> int:
    plugins_dir = home / "plugins"
    installed = (plugins_dir / PLUGIN_NAME / "__init__.py").exists()
    print(f"plugin installed: {installed}")
    print(f"plugin source:    {source if source.exists() else '(missing: pass --source)'}")
    print(f"memory.provider:  {read_provider_setting(home)}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=None,
                        help="plugin directory to install (default: repo integrations/hermes/ai-raccoon)")
    parser.add_argument("--python", default=None,
                        help="hermes runtime python for the discovery probe (default: auto)")
    parser.add_argument("--check", action="store_true",
                        help="report state without changing anything")
    args = parser.parse_args(argv)

    source = args.source or default_source()
    home = hermes_home()

    if args.check:
        return check(home, source)

    if not source.is_dir():
        print(f"ERROR: plugin source not found: {source}", file=sys.stderr)
        return 1

    plugins_dir = home / "plugins"
    print(f"[setup] HERMES_HOME = {home}")
    install(source, plugins_dir)

    python = resolve_hermes_python(args.python)
    if python:
        run_probe(home, plugins_dir, python)
    else:
        print("[probe] WARNING: could not resolve the hermes runtime python; "
              "probe skipped (pass --python to enable)")

    try:
        activate(home)
    except RuntimeError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1

    wire_exclude_prefix()

    print()
    print("Done. The provider activates on your NEXT hermes session.")
    print("Rollback: hermes config set memory.provider holographic")
    return 0


if __name__ == "__main__":
    sys.exit(main())
