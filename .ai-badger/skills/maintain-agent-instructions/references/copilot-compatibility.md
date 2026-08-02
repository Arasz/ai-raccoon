# Copilot compatibility notes

GitHub Copilot CLI and Copilot coding agent use repository custom instruction files. The relevant standard locations include:

- `.github/copilot-instructions.md` for repository-wide instructions.
- `.github/instructions/**/*.instructions.md` for modular/path-scoped instructions.
- `AGENTS.md`, `CLAUDE.md`, and other agent instruction files in standard discovery locations.

A project scaffolded with this skill typically keeps:

- `.github/copilot-instructions.md` as the Copilot repo-wide review/coding policy.
- `.github/instructions/*.instructions.md` as the detailed path-scoped source of implementation rules.
- `.junie/AGENTS.md` as the Junie adapter (if Junie is in use).
- `CLAUDE.md` as Claude Code's compact hub.

The maintenance scripts are plain command-line validators. They can be run by Claude Code, by a Copilot-driven terminal session, or by a CI workflow without depending on Claude-specific APIs.

CI integration should run the same scripts when any agent instruction or agent-instructions model file changes.
