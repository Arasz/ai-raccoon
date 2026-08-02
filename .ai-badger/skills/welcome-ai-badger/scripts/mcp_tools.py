"""MCP servers, one of the scaffold's collaborators.

Collects the servers the mcp catalog declares — `stack-mcp.json` and nothing else since
ADR-0014 step 8 — and writes them into the two project config files ai-badger owns:
`.mcp.json` and `.github/mcp.json`, the Copilot CLI's repo-committed config (#189). Every
user-global destination is proposed and never written (ADR-0014 decision 6) —
`~/.claude/settings.json` here, `~/.hermes/config.yaml` in the Hermes adjustment. A server
named by `config.mcp.decline` is not declared at all, and is removed from either file if an
earlier run wrote it (#186).

The Copilot CLI reads **both** project files — `.github/mcp.json` and `.mcp.json`, looked up
from the cwd upward — and their precedence is undocumented, so one server described twice has no
knowable configuration. The two entries are therefore identical apart from destination-specific
fields, and a server whose two renderings cannot be reconciled is declared once, in `.mcp.json`,
and named in a note (#193).
"""
from __future__ import annotations

import json as _json
import os
import shutil
from pathlib import Path
from typing import Any, Dict, List, NamedTuple, Optional, Sequence, Tuple

import config_guard as cg
from scaffold_context import ScaffoldContext

# Directories user-level package managers install executables into that a non-login
# process does not have on PATH.  (probe directory, ``${HOME}``-relative prefix emitted
# into the config).  See docs/changelog/0.28.0-mcp-user-tool-paths.md.
USER_TOOL_DIRS = (
    (Path.home() / ".dotnet" / "tools", "${HOME}/.dotnet/tools"),
    (Path.home() / ".local" / "bin", "${HOME}/.local/bin"),
)

# The keys the renderer below emits into an MCP entry, and the authorship test for a file
# ai-badger is about to retire: anything else in it came from a hand (:func:`only_generated_entries`).
GENERATED_ENTRY_KEYS = frozenset({"command", "args", "cwd", "env", "tools"})

# Written until 0.51.0 and read by no Copilot surface (#189); retired on the next scaffold.
LEGACY_COPILOT_MCP_CONFIG = (".github", "copilot", "mcp-config.json")

# The two per-stack declaration files ADR-0014 replaced with `stack-mcp.json`. No reader
# consults them any more, so a stack that still ships one is named in a note: an overlay
# whose servers silently stopped being declared is the failure this list exists to prevent.
RETIRED_DECLARATION_FILES = ("mcp-servers.json", "external-tools.json")


def only_generated_entries(data: Dict[str, Any]) -> bool:
    """Whether *data* is exactly the shape this module's writer produces, and nothing more.

    The test behind retiring a generated config file: a top level of `mcpServers` alone, and
    entries carrying only :data:`GENERATED_ENTRY_KEYS`. A `type`, `url` or `inputs` key is
    valid Copilot configuration that ai-badger has never written, so the file is someone's own.

    Kept for the one migration that shipped before the record existed (#189). A new retirement
    should read the manifest's `generatedConfig` instead: from 0.52.0 every destination write is
    recorded there, so authorship is proven rather than inferred from key shape (#194).
    """
    if set(data) - {"mcpServers"}:
        return False
    section = data.get("mcpServers", {})
    if not isinstance(section, dict):
        return False
    return all(isinstance(entry, dict) and not set(entry) - GENERATED_ENTRY_KEYS
               for entry in section.values())


def split_on_whitespace(command: str) -> Tuple[str, List[str]]:
    """Split *command* into ``(executable, args)``: every word after the first is an argument.

    The one splitter every destination uses: both hosts' schemas describe an executable plus an
    ``args`` array, and two files that split a command differently declare different servers
    (#193). A declaration that spells out ``args`` is never split at all.
    """
    parts = command.split()
    if not parts:
        return command, []
    return parts[0], parts[1:]


class McpDestination(NamedTuple):
    """One generated MCP config file — these columns are the only differences between them."""

    label: str
    # The agents that read this file, most authoritative first: the first one that is
    # configured supplies the agentOverrides applied here (F-22).
    readers: Tuple[str, ...]
    requires_reader: bool  # written only for a configured reader, vs written regardless
    pin_cwd: bool
    expand_home: bool  # rewrite a user-tool-dir command to ``${HOME}`` form
    all_tools: bool  # carry the per-server ``tools`` allowlist — Copilot's field, inert for Claude
    consequence: str  # what a refusal to write costs, for the note


# ``.mcp.json`` alone expands ``${VAR}`` (documented by Claude Code). The Copilot CLI reads it
# too, by cwd-upward lookup, which is why it carries the ``tools`` allowlist and why its overrides
# fall back to Copilot's when Claude is not configured (#193).
MCP_JSON = McpDestination(
    label=".mcp.json", readers=("claude", "copilot"), requires_reader=False, pin_cwd=False,
    expand_home=True, all_tools=True,
    consequence=".mcp.json not updated",
)
# The Copilot CLI's repo-committed config (#189): the same entry ``.mcp.json`` carries, minus
# the ``cwd`` pin Copilot does not document.
COPILOT_MCP_JSON = McpDestination(
    label=".github/mcp.json", readers=("copilot",), requires_reader=True, pin_cwd=False,
    expand_home=False, all_tools=True,
    consequence="copilot MCP config not updated",
)
CLAUDE_USER_SETTINGS = McpDestination(
    label="~/.claude/settings.json", readers=("claude",), requires_reader=True, pin_cwd=False,
    expand_home=False, all_tools=False,
    consequence="claude user MCP servers not proposed",
)


class McpTools:
    """Collects, merges and writes MCP server declarations into each agent's config file."""

    def __init__(self, ctx: ScaffoldContext):
        self.ctx = ctx

    # -- the mcp catalog ---------------------------------------------------------------

    def collect_catalog_mcp_servers(self) -> List[Dict[str, Any]]:
        """Read stack-mcp.json from features/common/ and each active stack.

        Common first, then stacks in config order, last writer wins on name (a later stack
        overrides an earlier one).
        """
        result = []  # type: List[Dict[str, Any]]
        for stack in self.ctx.stacks:
            path = self.ctx.root / "features" / stack / "stack-mcp.json"
            if not path.exists():
                continue
            try:
                data = _json.loads(path.read_text(encoding="utf-8"))
            except (ValueError, OSError) as exc:
                self.ctx.notes.append(
                    f"features/{stack}/stack-mcp.json is unreadable "
                    f"({type(exc).__name__}) — its entries were not scaffolded"
                )
                continue
            for srv in data.get("servers", []):
                result = [s for s in result if s.get("name") != srv.get("name")]
                result.append(srv)
        return result

    def _server_instructions(self, name: str) -> str:
        """`server.md` for a catalog server, found through the index, or '' when there is none."""
        import badger_lib as bl

        for stack in self.ctx.stacks:
            for item in bl.feature_items(self.ctx.index, stack, "mcp"):
                if item.get("name") != name:
                    continue
                doc = self.ctx.root / item.get("path", "") / "server.md"
                if doc.is_file():
                    return doc.read_text(encoding="utf-8")
                return ""
        return ""

    def _server_prerequisite(self, name: str) -> Dict[str, Any]:
        """The `prerequisite` block from a catalog server's meta.json, or {} when it has none."""
        import badger_lib as bl

        for stack in self.ctx.stacks:
            for item in bl.feature_items(self.ctx.index, stack, "mcp"):
                if item.get("name") != name:
                    continue
                meta = self.ctx.root / item.get("path", "") / "meta.json"
                if not meta.is_file():
                    return {}
                try:
                    return _json.loads(meta.read_text(encoding="utf-8")).get("prerequisite", {})
                except (OSError, ValueError):
                    return {}
        return {}

    @staticmethod
    def _availability_command(server: Dict[str, Any]) -> Optional[str]:
        """Return the optional executable gate declared by a server."""
        availability = server.get("availability") or {}
        command = availability.get("command")
        return command if isinstance(command, str) and command else None

    def _server_available(self, server: Dict[str, Any]) -> bool:
        """Whether the server's optional executable gate resolves on PATH."""
        command = self._availability_command(server)
        return command is None or shutil.which(command) is not None

    def _unavailable_servers(self) -> List[str]:
        """Return declared server names whose optional executable is unavailable."""
        return [srv["name"] for srv in self.collect_catalog_mcp_servers()
                if srv.get("declare") and srv.get("name")
                and not self._server_available(srv)]

    def note_declared_prerequisites(self, names) -> None:
        """Name what each declared server needs installed, once per run.

        Takes the post-decline set rather than reading the catalog again: telling someone to
        install a prerequisite for a server they declined is noise, and the decline filtering
        is the caller's. Only declared servers, because those are the ones whose launch config
        ai-badger writes — a missing prerequisite surfaces later as the agent's own connection
        error, which names neither this framework nor the thing to install.
        """
        if self.ctx.mcp_prereqs_noted:
            return
        self.ctx.mcp_prereqs_noted = True
        for name in sorted(names):
            prereq = self._server_prerequisite(name)
            summary = (prereq or {}).get("summary")
            if not summary:
                # The schema requires `summary`, but nothing validates meta.json at scaffold
                # time — a hand-edited or half-written catalog entry must not take the whole
                # scaffold down for the sake of a note.
                continue
            parts = [f"{name} needs {summary}"]
            if prereq.get("check"):
                parts.append(f"check: {prereq['check']}")
            if prereq.get("install"):
                parts.append(f"install: {prereq['install']}")
            self.ctx.notes.append(
                "prerequisite — " + "; ".join(parts) + ". ai-badger declares the server, it "
                "does not install it."
            )

    def fill_mcp_described(self) -> None:
        """Fill ``ctx.mcp_described`` once: each declared server plus its ``server.md`` prose.

        A declaration naming no catalog directory is reported rather than silently dropped —
        the name is the join between the two files and a typo in it is invisible otherwise.
        """
        if self.ctx.mcp_described_filled:
            return
        described = []  # type: List[Dict[str, Any]]
        for srv in self.collect_catalog_mcp_servers():
            if not self._server_available(srv):
                continue
            name = srv.get("name")
            instructions = self._server_instructions(name) if name else ""
            if not instructions:
                self.ctx.notes.append(
                    f"stack-mcp.json declares '{name}', which names no mcp catalog entry with a "
                    f"server.md — no instructions were injected for it"
                )
            entry = dict(srv)
            entry["instructions"] = instructions
            described.append(entry)
        self.ctx.mcp_described = described
        self.ctx.mcp_described_filled = True

    def note_retired_declaration_files(self) -> None:
        """Name a stack still shipping a :data:`RETIRED_DECLARATION_FILES` file, once per run.

        Presence is the whole test: the file is never parsed, so a stale overlay hears that
        its servers stopped being declared instead of discovering it from an empty `.mcp.json`.
        """
        if self.ctx.mcp_retired_files_noted:
            return
        self.ctx.mcp_retired_files_noted = True
        for stack in self.ctx.stacks:
            for filename in RETIRED_DECLARATION_FILES:
                if not (self.ctx.root / "features" / stack / filename).exists():
                    continue
                self.ctx.notes.append(
                    f"features/{stack}/{filename} is no longer read (ADR-0014 step 8) — move "
                    f"the servers it declared into features/{stack}/stack-mcp.json, where "
                    f"'declare: true' is what writes a launch config now"
                )

    def declined_servers(self) -> List[str]:
        """Server names ``config.mcp.decline`` refuses, in order, blanks dropped (#186)."""
        mcp = self.ctx.config.get("mcp") or {}
        return [name for name in (mcp.get("decline") or []) if name]

    def declared_servers(self) -> Dict[str, Dict[str, Any]]:
        """Every server whose launch config ai-badger writes, keyed by name.

        One reader: ``stack-mcp.json``'s ``declare: true`` (ADR-0014 step 8 removed the two
        legacy ones; :meth:`note_retired_declaration_files` reports what they left behind).

        A name in ``config.mcp.decline`` is dropped: declining a server ai-badger declared in
        the same run is a contradiction the generated files should not carry (#186).
        """
        self.note_retired_declaration_files()
        declared = {srv["name"]: dict(srv) for srv in self.collect_catalog_mcp_servers()
                    if srv.get("declare") and self._server_available(srv)}
        for name in self.declined_servers():
            declared.pop(name, None)
        self.note_declared_prerequisites(declared)
        return declared

    def declarations_for_agent(self, agent: str) -> Dict[str, Dict[str, Any]]:
        """Declared servers resolved for *agent*'s own ``agentOverrides``, keyed by name.

        What an adjustment is handed: it is loaded by path and cannot resolve
        ``stack-mcp.json`` itself. Scope does not filter — the two hosts that only ever
        receive a *proposal* have no project route for scope to select between.
        """
        return {name: self._resolve_server_for_agent(srv, agent)
                for name, srv in self.declared_servers().items()}

    def project_server_names(self) -> List[str]:
        """The declared, project-scoped servers, sorted — what this project's own config gets."""
        project, _ = self.split_servers_by_scope(self.declared_servers())
        return sorted(project)

    def split_servers_by_scope(
        self, servers: Dict[str, Dict[str, Any]]
    ) -> Tuple[Dict[str, Dict[str, Any]], Dict[str, Dict[str, Any]]]:
        """Split merged servers into (project_servers, user_servers).

        Default scope is ``"project"``.
        """
        project = {}  # type: Dict[str, Dict[str, Any]]
        user = {}  # type: Dict[str, Dict[str, Any]]
        for name, srv in servers.items():
            if srv.get("scope", "project") == "user":
                user[name] = srv
            else:
                project[name] = srv
        return project, user

    def _destination_applies(
        self, dest: McpDestination, servers: Dict[str, Dict[str, Any]],
        declined: Sequence[str] = (),
    ) -> bool:
        """Whether *dest* is touched at all: it needs something to say, and may need a reader.

        A run with nothing left to declare still has something to say when a server was
        declined — the entry an earlier run wrote has to come back out (#186).
        """
        if not servers and not declined:
            return False
        return not dest.requires_reader or self._configured_reader(dest) is not None

    def _configured_reader(self, dest: McpDestination) -> Optional[str]:
        """*dest*'s most authoritative reading agent that this project configures, or None."""
        agents = self.ctx.config.get("agents", [])
        for reader in dest.readers:
            if reader in agents:
                return reader
        return None

    def _override_reader(
        self, dest: McpDestination, servers: Dict[str, Dict[str, Any]]
    ) -> Optional[str]:
        """Return *dest*'s configured reader, else None + a note if overrides drop.

        A generated file's overrides are its reading agent's — never whichever agent happens
        to come first in config.agents (F-22). Where two hosts read one file, the first
        configured reader in :attr:`McpDestination.readers` wins.
        """
        reader = self._configured_reader(dest)
        if reader is not None:
            return reader
        dropped = sorted(name for name, srv in servers.items() if srv.get("agentOverrides"))
        if dropped:
            self.ctx.notes.append(
                f"{dest.label} written without agent overrides ({', '.join(dropped)}) — it is "
                f"read by {' and '.join(dest.readers)}, and config.agents names none of them"
            )
        return None

    def _resolve_server_for_agent(
        self, server: Dict[str, Any], agent_name: str
    ) -> Dict[str, Any]:
        """Apply agentOverrides for *agent_name* and return resolved dict."""
        overrides = server.get("agentOverrides", {})
        agent_ovr = overrides.get(agent_name)
        if agent_ovr:
            resolved = dict(server)
            resolved.update(agent_ovr)
            return resolved
        return dict(server)

    def _render_entries(
        self, servers: Dict[str, Dict[str, Any]], dest: McpDestination
    ) -> Dict[str, Dict[str, Any]]:
        """Render *servers* as *dest*'s config entries, resolved for its reading agent."""
        reader = self._override_reader(dest, servers)
        entries = {
            name: self._render_entry(
                self._resolve_server_for_agent(srv, reader) if reader else dict(srv), dest)
            for name, srv in servers.items()
        }
        if dest.expand_home:
            self._home_relative_commands(entries)
        return entries

    def _render_without_notes(
        self, servers: Dict[str, Dict[str, Any]], dest: McpDestination
    ) -> Dict[str, Dict[str, Any]]:
        """Render *servers* for *dest* to compare against, without repeating its notes.

        The destination that owns the write has already said whatever there was to say about
        these servers; a second render exists only to be compared with (#193).
        """
        spoken = self.ctx.notes
        self.ctx.notes = []
        try:
            return self._render_entries(servers, dest)
        finally:
            self.ctx.notes = spoken

    def _render_entry(self, srv: Dict[str, Any], dest: McpDestination) -> Dict[str, Any]:
        """Render one resolved server declaration as one *dest* entry."""
        entry = {}  # type: Dict[str, Any]
        if "args" in srv:
            entry["command"] = srv.get("command", "")
            entry["args"] = srv["args"]
        else:
            exe, args = split_on_whitespace(srv.get("command", ""))
            entry["command"] = exe
            if args:
                entry["args"] = args
        if dest.pin_cwd:
            entry["cwd"] = str(self.ctx.target)
        if "env" in srv:
            entry["env"] = srv["env"]
        if dest.all_tools:
            entry["tools"] = ["*"]
        return entry

    def _home_relative_command(self, name: str, command: str) -> str:
        """Rewrite a bare executable that lives in a user tool dir to its ``${HOME}`` form.

        Leaves anything already pathed, already expandable, or resolvable elsewhere on PATH
        alone; notes a command that resolves nowhere.
        """
        parts = command.split(maxsplit=1)
        if not parts:
            return command
        executable = parts[0]
        if "/" in executable or executable.startswith("${"):
            return command
        for probe_dir, prefix in USER_TOOL_DIRS:
            candidate = Path(probe_dir) / executable
            if candidate.is_file() and os.access(str(candidate), os.X_OK):
                suffix = f" {parts[1]}" if len(parts) > 1 else ""
                return prefix + "/" + executable + suffix
        if shutil.which(executable) is None:
            self.ctx.notes.append(
                f"MCP server '{name}' command '{executable}' was not found on PATH or in any "
                f"known user tool directory — that server will fail to start"
            )
        return command

    def _home_relative_commands(
        self, entries: Dict[str, Dict[str, Any]]
    ) -> Dict[str, Dict[str, Any]]:
        """Apply :meth:`_home_relative_command` to each rendered entry's executable."""
        for name, entry in entries.items():
            entry["command"] = self._home_relative_command(name, entry.get("command", ""))
        return entries

    def _carry_live_cwd(
        self, section: Dict[str, Any], entries: Dict[str, Dict[str, Any]]
    ) -> None:
        """Keep a recorded ``cwd`` that still names a scaffolded project.

        ``.mcp.json`` is tracked, so re-scaffolding from a second checkout would otherwise
        restage every entry's ``cwd`` onto a directory the original checkout never used.
        """
        for name, entry in entries.items():
            recorded = section.get(name, {}).get("cwd")
            if not isinstance(recorded, str) or not recorded:
                continue
            if recorded != entry.get("cwd") and (Path(recorded) / ".ai-badger").is_dir():
                entry["cwd"] = recorded
                self.ctx.notes.append(
                    f"MCP server '{name}': kept the recorded cwd {recorded} rather than "
                    f"repointing it at {self.ctx.target}"
                )

    def _merge_mcp_servers_json(
        self,
        path: Path,
        entries: Dict[str, Dict[str, Any]],
        dest: McpDestination,
        declined: Sequence[str] = (),
        elsewhere: Sequence[str] = (),
        unavailable: Sequence[str] = (),
    ) -> bool:
        """Merge rendered *entries* into the ``mcpServers`` object of the JSON file at *path*.

        *declined* names come back out of the section, so a decline reaches a file an earlier
        run already wrote; so do *elsewhere* names, the servers another file declares
        differently (#193).  Returns False without writing when the existing file is not a
        readable mapping, or when there is neither an entry to add nor a file to clean.
        """
        if not entries and not path.exists():
            return False
        existing, note = cg.read_json_mapping(path)
        section = cg.mapping_section(existing, "mcpServers") if existing is not None else None
        if section is None:
            note = note or cg.refusal(path, "mcpServers is not a mapping")
            self.ctx.notes.append(f"{note} ({dest.consequence})")
            return False
        if dest.pin_cwd:
            self._carry_live_cwd(section, entries)
        # A stale ``cwd`` needs no explicit removal: the rendered entry omits it unless
        # ``pin_cwd``, and ``update`` replaces the recorded entry outright. A server this
        # scaffold does not declare keeps whatever its author wrote (#291).
        section.update(entries)
        self._drop_declined(section, declined, dest)
        self._drop_declared_elsewhere(section, elsewhere, dest)
        self._drop_unavailable(section, unavailable, dest)
        cg.write_json_with_backup(path, existing)
        self.ctx.record_generated_config(path, dest.label)
        return True

    @staticmethod
    def _drop_servers(section: Dict[str, Any], names: Sequence[str]) -> List[str]:
        """Remove every named server from *section* and return the ones that were there."""
        removed = [name for name in names if name in section]
        for name in removed:
            section.pop(name)
        return removed

    def _drop_declined(
        self, section: Dict[str, Any], declined: Sequence[str], dest: McpDestination
    ) -> None:
        """Remove every declined server from *section*, reporting what left (#186)."""
        removed = self._drop_servers(section, declined)
        if removed:
            self.ctx.notes.append(
                f"{dest.label}: removed declined MCP server(s) {', '.join(removed)} — "
                f"config.mcp.decline names them")

    def _drop_declared_elsewhere(
        self, section: Dict[str, Any], names: Sequence[str], dest: McpDestination
    ) -> None:
        """Remove servers an earlier run declared here as well as in ``.mcp.json`` (#193)."""
        removed = self._drop_servers(section, names)
        if removed:
            self.ctx.notes.append(
                f"{dest.label}: removed MCP server(s) {', '.join(removed)} that an earlier run "
                f"declared here as well as in {MCP_JSON.label} — one server, one declaration")

    def _drop_unavailable(
        self, section: Dict[str, Any], names: Sequence[str], dest: McpDestination
    ) -> None:
        """Remove unavailable entries only when they match ai-badger's rendered shape."""
        catalog = {srv.get("name"): srv for srv in self.collect_catalog_mcp_servers()}
        generated = []
        for name in names:
            server = catalog.get(name)
            if server is None or name not in section:
                continue
            expected = self._render_entry(server, dest)
            candidates = [expected]
            if dest.expand_home:
                executable, args = split_on_whitespace(server.get("command", ""))
                for _probe_dir, prefix in USER_TOOL_DIRS:
                    home_entry = dict(expected)
                    home_entry["command"] = prefix + "/" + executable
                    if args:
                        home_entry["args"] = args
                    candidates.append(home_entry)
            if section[name] in candidates:
                generated.append(name)
        removed = self._drop_servers(section, generated)
        if removed:
            self.ctx.notes.append(
                f"{dest.label}: removed unavailable MCP server(s) {', '.join(removed)} — "
                "their optional executable is not installed")

    def propose_claude_mcp_user(
        self, user_servers: Dict[str, Dict[str, Any]]
    ) -> None:
        """Print the ``~/.claude/settings.json`` snippet for scope:user servers; never write it.

        ADR-0014 decision 6: ai-badger proposes user-global configuration and writes only the
        project-scoped files it owns.  Gated on ``"claude"`` in ``config.agents``.
        """
        if not self._destination_applies(CLAUDE_USER_SETTINGS, user_servers):
            return

        entries = self._render_entries(user_servers, CLAUDE_USER_SETTINGS)
        self.ctx.notes.append(
            f"{len(entries)} MCP server(s) are declared scope:user — ai-badger never writes "
            f"user-global agent configuration (ADR-0014 decision 6). To register them, merge "
            f"this into ~/.claude/settings.json yourself: "
            f"{_json.dumps({'mcpServers': entries}, ensure_ascii=False, sort_keys=True)}"
        )

    def generate_copilot_mcp_json(
        self, servers: Dict[str, Dict[str, Any]]
    ) -> None:
        """Generate ``.github/mcp.json`` — the config the Copilot CLI actually reads (#189).

        Merge-only.  Gated on ``"copilot"`` in ``config.agents``; the dead path it replaces is
        retired either way.  A server ``.mcp.json`` declares differently is left out and named
        instead, because the Copilot CLI reads that file too (#193).
        """
        self._retire_copilot_mcp_config()
        declined = self.declined_servers()
        unavailable = self._unavailable_servers()
        wanted = {name: srv for name, srv in servers.items() if name not in declined}
        if not self._destination_applies(COPILOT_MCP_JSON, wanted, declined + unavailable):
            return

        entries = self._render_entries(wanted, COPILOT_MCP_JSON)
        elsewhere = self._declared_differently_in_mcp_json(wanted, entries)
        for name in elsewhere:
            entries.pop(name)
        self._merge_mcp_servers_json(
            self.ctx.target / ".github" / "mcp.json",
            entries,
            COPILOT_MCP_JSON,
            declined,
            elsewhere,
            unavailable,
        )

    def _declared_differently_in_mcp_json(
        self, servers: Dict[str, Dict[str, Any]], entries: Dict[str, Dict[str, Any]]
    ) -> List[str]:
        """Names whose ``.mcp.json`` entry differs from *entries*, the ``cwd`` pin aside (#193).

        The Copilot CLI reads both files with no documented precedence, so a server the two
        describe differently has no knowable configuration.  It is declared once — in
        ``.mcp.json``, the file both hosts read — and named here for the note.
        """
        if not self._destination_applies(MCP_JSON, servers):
            return []
        theirs = self._render_without_notes(servers, MCP_JSON)
        elsewhere = []  # type: List[str]
        for name, entry in entries.items():
            counterpart = {key: value for key, value in theirs.get(name, {}).items()
                           if key != "cwd"}
            if name not in theirs or counterpart == entry:
                continue
            differing = sorted(key for key in set(counterpart) | set(entry)
                               if counterpart.get(key) != entry.get(key))
            elsewhere.append(name)
            self.ctx.notes.append(
                f"MCP server '{name}' is declared only in {MCP_JSON.label}: the entry "
                f"{COPILOT_MCP_JSON.label} would carry differs from it ({', '.join(differing)}), "
                f"and Copilot CLI reads both files with no documented precedence (issue #193)")
        return elsewhere

    def _retire_copilot_mcp_config(self) -> None:
        """Retire ``.github/copilot/mcp-config.json`` — no Copilot surface reads it (#189).

        Removed with a backup when it is the shape ai-badger's own writer produced; anything
        else is someone's own file and is reported, never deleted.
        """
        path = self.ctx.target.joinpath(*LEGACY_COPILOT_MCP_CONFIG)
        if not path.exists():
            return
        data, refusal = cg.read_json_mapping(path)
        if data is None or not only_generated_entries(data):
            self.ctx.notes.append(
                f"{path} is read by no Copilot surface (issue #189), and was not written by "
                f"ai-badger ({refusal or 'it carries keys ai-badger never writes'}) — left in "
                f"place; move anything you need into .github/mcp.json and delete it yourself")
            return
        backup = cg.remove_with_backup(path)
        self.ctx.notes.append(
            f"removed {path} — no Copilot surface reads it (issue #189); the servers it "
            f"declared are in .github/mcp.json now, and a copy is at {backup.name}")

    # -- orchestrate ----------------------------------------------------------------

    def generate_mcp_json(self) -> None:
        """Generate .mcp.json for every declared MCP server (:meth:`declared_servers`).

        Commands stay portable — no absolute paths; a bare executable found in a user tool
        directory is emitted ``${HOME}``-relative.  Only project-scoped servers are written.
        """
        project_servers, _ = self.split_servers_by_scope(self.declared_servers())
        declined = self.declined_servers()
        unavailable = self._unavailable_servers()

        if not self._destination_applies(MCP_JSON, project_servers, declined + unavailable):
            return

        mcp_servers = self._render_entries(project_servers, MCP_JSON)
        written = self._merge_mcp_servers_json(
            self.ctx.target / ".mcp.json", mcp_servers, MCP_JSON, declined,
            unavailable=unavailable
        )
        if written:
            self.ctx.notes.append(
                f"generated .mcp.json with {len(mcp_servers)} MCP server(s)"
            )
