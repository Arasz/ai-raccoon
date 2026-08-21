"""Apply retrieval knobs to a scratch bank through the `settings retrieval` CLI (plan §8).

Every knob is written EXPLICITLY via subprocess (`ai-raccoon --data-root <root>
--port <port> settings retrieval <verb> set <value>`) — the harness never relies
on inherited settings rows (the memory-db copy inherits fusion=true and
structureAlpha=0.5 from the live bank). The settings verbs dial a backend on the
given port: the running scratch server's port, or a picked-free one for one-shot
writes. Port 7721 (the live server) is refused.
"""

from __future__ import annotations

import os
import re
import signal
import socket
import subprocess
from typing import Optional

from .server import SafetyViolation, assert_port_not_7721

# The nine knobs and their canonical defaults (plan §1 table / §8 settings.py).
KNOB_DEFAULTS: dict = {
    "rrfK": 60,
    "ftsWeight": 1,
    "vectorWeight": 1,
    "sourceLambda": 0.1,
    "consolidationThreshold": 0.1,
    "docScoreFormula": "max",
    "candidateWindow": "max3x100",
    "structureAlpha": 0.5,
    "fusion": False,
}

_KNOB_VERBS = {
    "rrfK": "rrfk",
    "ftsWeight": "fts-weight",
    "vectorWeight": "vector-weight",
    "sourceLambda": "source-lambda",
    "consolidationThreshold": "consolidation",
    "docScoreFormula": "doc-formula",
    "candidateWindow": "window",
    "structureAlpha": "alpha",
    "fusion": "fusion",
}


class SettingsError(RuntimeError):
    """A settings subprocess exited non-zero."""


def knob_verb(knob: str) -> str:
    try:
        return _KNOB_VERBS[knob]
    except KeyError:
        raise ValueError(f"unknown knob {knob!r}; expected one of {sorted(_KNOB_VERBS)}") from None


def _format_value(knob: str, value) -> str:
    if isinstance(value, bool):
        raise ValueError(f"knob {knob} is a bool; use the fusion enable/disable verbs")
    if isinstance(value, float):
        return repr(float(value))
    return str(value)


def settings_command(knob: str, value=None, action: str = "set") -> list[str]:
    """The verb argv after --data-root/--port: e.g. ['settings','retrieval','rrfk','set','60']."""
    verb = knob_verb(knob)
    if verb == "fusion":
        if action == "show":
            return ["settings", "retrieval", "fusion", "show"]
        if action == "set":
            if value is True:
                return ["settings", "retrieval", "fusion", "enable"]
            if value is False:
                return ["settings", "retrieval", "fusion", "disable"]
            raise ValueError(f"fusion value must be a bool, got {value!r}")
        raise ValueError(f"fusion supports only set/show, not {action!r}")
    if action == "show":
        return ["settings", "retrieval", verb, "show"]
    if action != "set":
        raise ValueError(f"unsupported action {action!r} (expected set or show)")
    return ["settings", "retrieval", verb, "set", _format_value(knob, value)]


def show_all_command() -> list[str]:
    return ["settings", "retrieval", "show-all"]


def pick_free_port() -> int:
    """An ephemeral port for one-shot settings writes (never 7721 by construction)."""
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def settings_argv(
    binary: str,
    data_root,
    knob: str,
    value=None,
    action: str = "set",
    port: Optional[int] = None,
) -> list[str]:
    """The full argv for one settings operation, with the safety asserts applied."""
    port = port if port is not None else pick_free_port()
    assert_port_not_7721(port)
    return [
        binary,
        "--data-root", str(data_root),
        "--port", str(port),
        *settings_command(knob, value, action),
    ]


def _run(argv: list[str]) -> list[str]:
    try:
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=120)
    except subprocess.TimeoutExpired as exc:
        raise SettingsError(f"settings command timed out: {' '.join(argv)}") from exc
    if proc.returncode != 0:
        raise SettingsError(
            f"settings command failed (exit {proc.returncode}): {' '.join(argv)}\n"
            f"{proc.stderr.strip()[:500]}"
        )
    return [line for line in proc.stdout.splitlines() if line.strip()]


def _stop_one_shot_backend(port: int) -> None:
    """SIGTERM the proxy backend a standalone settings write launched.

    Measured: `settings ... --port <free>` starts a backend on that port which
    outlives the CLI (idle watchdog 4h). The harness's server-attached path
    passes the scratch server's own port and never leaks; only auto-picked
    one-shot ports need this cleanup.
    """
    try:
        out = subprocess.run(
            ["lsof", "-ti", f"tcp:{port}"], capture_output=True, text=True, timeout=10
        )
    except (OSError, subprocess.TimeoutExpired):
        return
    for pid in out.stdout.split():
        try:
            os.kill(int(pid), signal.SIGTERM)
        except (ValueError, ProcessLookupError):
            pass


def apply_settings(data_root, knob_dict: dict, port: Optional[int] = None, binary: str = "ai-raccoon") -> list[str]:
    """Write every knob in knob_dict to the bank; returns the CLI stdout lines."""
    auto_port = port is None
    port = port if port is not None else pick_free_port()
    assert_port_not_7721(port)
    output: list[str] = []
    for knob, value in knob_dict.items():
        output.extend(_run(settings_argv(binary, data_root, knob, value, "set", port)))
    if auto_port:
        _stop_one_shot_backend(port)
    return output


def reset_to_defaults(data_root, port: Optional[int] = None, binary: str = "ai-raccoon") -> list[str]:
    """Write ALL NINE defaults explicitly — never the copy's inherited state."""
    return apply_settings(data_root, dict(KNOB_DEFAULTS), port=port, binary=binary)


def show_all(data_root, port: Optional[int] = None, binary: str = "ai-raccoon") -> str:
    auto_port = port is None
    port = port if port is not None else pick_free_port()
    assert_port_not_7721(port)
    argv = [binary, "--data-root", str(data_root), "--port", str(port), *show_all_command()]
    output = "\n".join(_run(argv))
    if auto_port:
        _stop_one_shot_backend(port)
    return output


def show_knob(data_root, knob: str, port: Optional[int] = None, binary: str = "ai-raccoon") -> str:
    auto_port = port is None
    port = port if port is not None else pick_free_port()
    assert_port_not_7721(port)
    argv = [binary, "--data-root", str(data_root), "--port", str(port),
            *settings_command(knob, action="show")]
    output = "\n".join(_run(argv))
    if auto_port:
        _stop_one_shot_backend(port)
    return output


def parse_show_all(text: str) -> dict:
    """Parse `settings retrieval show-all` lines ('rrfK: 5  (setting)') into name -> (value, source)."""
    parsed: dict = {}
    for line in text.splitlines():
        match = re.match(r"^(\w+): (\S+)  \(([a-z]+)\)", line.strip())
        if match:
            parsed[match.group(1)] = (match.group(2), match.group(3))
    return parsed


def parse_fusion_show(text: str) -> bool:
    """Parse `settings retrieval fusion show` ('enabled: True  (default: False — ...)')."""
    match = re.search(r"enabled:\s*(True|False)", text)
    if not match:
        raise ValueError(f"cannot parse fusion show output: {text[:200]!r}")
    return match.group(1) == "True"
