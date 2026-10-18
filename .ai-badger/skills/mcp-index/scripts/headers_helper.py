#!/usr/bin/env python3
"""Run an MCP server's `headersHelper` the way Claude Code does: `run(command, ...)`.

Claude Code's contract (code.claude.com/docs/en/mcp, "headersHelper"): the command runs in a
shell, is given up on after 10 seconds, and must print a JSON object of string headers, which
override static `headers` of the same name. Its cwd depends on where the server is declared,
`CLAUDE_CODE_MCP_SERVER_NAME`/`_URL` (and `CLAUDE_PLUGIN_ROOT` for a plugin) are set, and a
helper a repository or plugin supplies runs without credential-looking environment variables.
"""

from __future__ import annotations

import json
import os
import re
import signal
import subprocess
from typing import Dict, Mapping, Optional

TIMEOUT = 10.0
NAME_VAR, URL_VAR, PLUGIN_ROOT_VAR = ("CLAUDE_CODE_MCP_SERVER_NAME", "CLAUDE_CODE_MCP_SERVER_URL",
                                      "CLAUDE_PLUGIN_ROOT")
# "a name with TOKEN, SECRET, PASSWORD, KEY, or AUTH in it in either letter case", Git's
# GIT_CONFIG_KEY_<n> excepted, plus credential names that do not follow the pattern.
_CREDENTIAL = re.compile(r"TOKEN|SECRET|PASSWORD|KEY|AUTH", re.IGNORECASE)
_GIT_CONFIG_KEY = re.compile(r"GIT_CONFIG_KEY_\d+")
_ALWAYS_REMOVED = {"ANTHROPIC_CUSTOM_HEADERS"}
BAD_OUTPUT = "output is not a JSON object of string headers"


class HeadersHelperError(RuntimeError):
    """The helper failed; the message is one line naming why."""


def _one_line(text: str, limit: int = 160) -> str:
    flat = " ".join(str(text).split())
    return flat if len(flat) <= limit else flat[:limit - 1] + "…"


def is_credential(name: str) -> bool:
    """Whether Claude Code strips this variable from an untrusted helper's environment."""
    return name in _ALWAYS_REMOVED or (bool(_CREDENTIAL.search(name))
                                       and not _GIT_CONFIG_KEY.fullmatch(name))


def helper_env(base: Mapping[str, str], *, name: str, url: str, scrub: bool,
               plugin_root: Optional[str]) -> Dict[str, str]:
    """The helper's environment: `base`, minus credentials when scrubbed, plus the server vars.

    A scrubbed helper also gets the URL with any removed variable's live value redacted.
    """
    env = dict(base)
    if scrub:
        removed = {key: env.pop(key) for key in list(env) if is_credential(key)}
        for value in removed.values():
            if len(value) >= 4:
                url = url.replace(value, "REDACTED")
    env[NAME_VAR], env[URL_VAR] = name, url
    if plugin_root:
        env[PLUGIN_ROOT_VAR] = plugin_root
    return env


def _parse(stdout: bytes) -> Dict[str, str]:
    try:
        headers = json.loads(stdout.decode("utf-8") or "null")
    except (UnicodeDecodeError, ValueError) as exc:
        raise HeadersHelperError(BAD_OUTPUT) from exc
    if not isinstance(headers, dict) or not all(
            isinstance(k, str) and isinstance(v, str) and k and not re.search(r"[\r\n]", k + v)
            for k, v in headers.items()):
        raise HeadersHelperError(BAD_OUTPUT)
    return headers


def run(command: str, *, cwd: str, env: Mapping[str, str],
        timeout: Optional[float] = None) -> Dict[str, str]:
    """The helper's headers; raises HeadersHelperError. Its process group dies on timeout."""
    limit = TIMEOUT if timeout is None else timeout
    try:
        proc = subprocess.Popen(  # pylint: disable=consider-using-with
            command, shell=True, cwd=cwd, env=dict(env), stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=os.name == "posix")
    except OSError as exc:
        raise HeadersHelperError(f"could not start: {_one_line(exc)}") from exc
    try:
        stdout, stderr = proc.communicate(timeout=limit)
    except subprocess.TimeoutExpired as exc:
        try:
            if os.name == "posix":
                os.killpg(proc.pid, signal.SIGKILL)
            else:
                proc.kill()
        except OSError:
            pass
        proc.communicate()
        raise HeadersHelperError(f"timed out after {limit:g}s") from exc
    if proc.returncode != 0:
        last = stderr.decode("utf-8", "replace").strip().splitlines()
        raise HeadersHelperError(f"exited {proc.returncode}: "
                                 f"{_one_line(last[-1]) if last else 'no stderr'}")
    return _parse(stdout)
