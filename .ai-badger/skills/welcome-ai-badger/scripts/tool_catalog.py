"""Opt-in CLI tools (the ``tools`` feature type, ADR-0039), one of the scaffold's collaborators.

Reads ``config.tools``, joins each name to its catalog ``meta.json``, names the prerequisite
with absolute framework paths, runs its check (and install, when the check fails) only under
``--execute``, and resolves each opted-in agent's arm for the adjustments. Native arms depend
on config alone and never probe PATH, so the scaffold output is the same with or without the
tool installed.
"""
from __future__ import annotations

import json
import os
import shlex
import subprocess
import sys
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

import badger_lib as bl
from scaffold_context import ScaffoldContext
from tool_hooks import guarded_command

CHECK_TIMEOUT = 30
INSTALL_TIMEOUT = 900


def find_tool(ctx: ScaffoldContext, name: str) -> Optional[tuple]:
    """``(tool dir, meta)`` for a catalog tool in an active stack, or None."""
    for stack in ctx.stacks:
        for item in bl.feature_items(ctx.index, stack, "tools"):
            if item.get("name") != name:
                continue
            path = ctx.root / item.get("path", "")
            try:
                return path, json.loads((path / "meta.json").read_text(encoding="utf-8"))
            except (OSError, ValueError):
                return None
    return None


def tool_instructions(ctx: ScaffoldContext) -> List[Dict[str, str]]:
    """``[{"instructions": tool.md}]`` for each catalog tool ``config.tools`` names, in order.

    Depends on config alone, never on PATH or the platform, so the agent files are the same
    wherever they are scaffolded.
    """
    entries = []
    for name in ctx.config.get("tools") or {}:
        found = find_tool(ctx, name)
        if found is None:
            continue
        doc = found[0] / "tool.md"
        if doc.is_file():
            entries.append({"instructions": doc.read_text(encoding="utf-8")})
    return entries


class ToolCatalog:
    """Collects enabled tools and the per-agent arm each adjustment consumes."""

    def __init__(self, ctx: ScaffoldContext, execute: bool = False,
                 run: Callable[..., Any] = subprocess.run):
        self.ctx = ctx
        self.execute = execute
        self.run = run
        self._tools: Optional[Dict[str, Dict[str, Any]]] = None

    def prepare(self) -> Dict[str, Dict[str, Any]]:
        """Resolve every enabled tool once: ``{name: {meta, agents, check_ok}}``."""
        if self._tools is not None:
            return self._tools
        self._tools = {}
        for name, entry in (self.ctx.config.get("tools") or {}).items():
            found = self._find(name)
            if found is None:
                self.ctx.notes.append(
                    f"config.tools names '{name}', which is no tool in the catalog — skipped")
                continue
            path, meta = found
            prereq = meta.get("prerequisite") or {}
            self._note_prerequisite(name, path, prereq)
            check_ok = self._check_and_install(name, path, prereq) if self.execute else None
            agents = self._agents(name, meta, (entry or {}).get("hooks") or [], check_ok)
            self._tools[name] = {"meta": meta, "agents": agents, "check_ok": check_ok}
        return self._tools

    def _find(self, name: str) -> Optional[tuple]:
        return find_tool(self.ctx, name)

    def _absolute(self, command: str) -> List[str]:
        """*command* split, with each repo-relative ``features/`` token made absolute."""
        return [str(self.ctx.root / word) if word.startswith("features/") else word
                for word in shlex.split(command)]

    def _note_prerequisite(self, name: str, path: Path, prereq: Dict[str, Any]) -> None:
        """Name what the tool needs, with commands a consumer repo can run as printed."""
        parts = [f"{name} needs {prereq.get('summary') or 'its prerequisite'}"]
        for key in ("check", "install"):
            if prereq.get(key):
                parts.append(f"{key}: {shlex.join(self._absolute(prereq[key]))}")
        self.ctx.notes.append(
            "prerequisite — " + "; ".join(parts) + f". See {path / 'tool.md'}; ai-badger "
            "runs the check and install only under --execute.")

    def _run_step(self, name: str, step: str, command: str, timeout: int) -> bool:
        """Run one prerequisite command through this interpreter; True on exit 0."""
        argv = self._absolute(command)
        if argv and argv[0] in ("python", "python3"):
            argv[0] = sys.executable
        try:
            proc = self.run(argv, stdin=subprocess.DEVNULL, capture_output=True, text=True,
                            timeout=timeout, cwd=str(self.ctx.target), check=False)
        except (subprocess.SubprocessError, OSError) as exc:
            self.ctx.notes.append(f"{name} {step} failed ({type(exc).__name__})")
            return False
        if proc.returncode != 0 and step == "install":
            detail = (proc.stderr or "").strip()
            self.ctx.notes.append(f"{name} install failed (exit {proc.returncode})"
                                  + (f": {detail}" if detail else ""))
        return proc.returncode == 0

    def _check_and_install(self, name: str, path: Path, prereq: Dict[str, Any]) -> bool:
        """Check; install when the check fails, then check again. False without a check."""
        check = prereq.get("check")
        if not check:
            return False
        if self._run_step(name, "check", check, CHECK_TIMEOUT):
            return True
        install = prereq.get("install")
        if not install or not self._run_step(name, "install", install, INSTALL_TIMEOUT):
            return False
        ok = self._run_step(name, "check", check, CHECK_TIMEOUT)
        if not ok:
            self.ctx.notes.append(f"{name} installed but its check still fails — see "
                                  f"{path / 'tool.md'}")
        return ok

    def _agents(self, name: str, meta: Dict[str, Any], hooks: List[str],
                check_ok: Optional[bool]) -> Dict[str, Dict[str, Any]]:
        """Each opted-in, configured agent's arm; nothing at all on Windows."""
        if os.name == "nt":
            if hooks:
                self.ctx.notes.append(f"{name} hooks are POSIX-only — nothing wired on Windows")
            return {}
        configured = self.ctx.config.get("agents", [])
        modes = meta.get("hooks") or {}
        agents: Dict[str, Dict[str, Any]] = {}
        for agent in hooks:
            spec = modes.get(agent)
            if agent not in configured:
                self.ctx.notes.append(f"config.tools.{name}.hooks names '{agent}', which "
                                      f"config.agents does not — skipped")
            elif not spec:
                self.ctx.notes.append(f"{name} declares no hook for '{agent}' — skipped")
            else:
                agents[agent] = self._arm(name, spec, check_ok)
        return agents

    def _arm(self, name: str, spec: Dict[str, Any], check_ok: Optional[bool]) -> Dict[str, Any]:
        """One agent's arm: the guarded command, the init gate, or a bare proposal."""
        arm = {"mode": spec["mode"], "arg": spec["arg"]}
        if spec["mode"] == "native":
            arm["command"] = guarded_command(name, spec["arg"])
        elif spec["mode"] == "init":
            arm["run"] = check_ok is True
        return arm
