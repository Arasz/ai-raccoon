# Default projectId resolution from the calling directory (cwd default)

**Status:** implemented (1.37.0) — deviations recorded in PR #594: the Out-of-scope premise
was false (tool schemas marked projectId REQUIRED; every tool now carries an optional
projectId), union-then-Ambiguous with canonical-form candidate dedup, `ingest.scope.global`
skip, and `projectId` casing in both refusal messages. · **Author:** Rafał Araszkiewicz (Arasz) with ox-alpha ·
**Date:** 2026-08-29 · **Origin:** ai-badger/pi integration follow-up (state.json.next item 1)

## Problem

Every MCP tool requires an explicit `projectId` — `ToolGate.RequireAsync` refuses blank ids
with `invalid-params: project_id is required`. That is correct for bank hygiene but wrong for
hosts whose MCP configuration is user-scope: pi's `~/.pi/agent/settings.json` serves **every**
project from one file, so no projectId can be baked into the launch config. Today every pi
session must know and pass the id on all ~20 tools; a stale or wrong id is one hallucinated
argument away. Claude Code projects can scope `.mcp.json` per repo; pi cannot.

## Design

**Rule: an omitted projectId defaults to the project whose registered directory surface
contains the server process's cwd; ambiguity refuses; explicit always wins.**

1. **Seam** — `ToolGate.RequireAsync` (src/AiRaccoon/Tools/ToolGate.cs), the single funnel all
   seven tool classes already share. The `string.IsNullOrWhiteSpace(projectId)` branch
   consults a new resolver instead of throwing directly.

2. **Resolver** — `IProjectIdResolver.ResolveAsync(CancellationToken)` returning
   `Resolved(id) | Ambiguous(IReadOnlyList<string>) | None`, implemented as
   `CwdProjectIdResolver`:
   - Probe path = `Environment.CurrentDirectory` (where the host spawned the stdio server —
     the project dir for pi, claude, and hermes alike).
   - Candidate surface = each registered project's **ingest-scope paths** (the authoritative
     per-project mapping the ingest tools already enforce) plus, secondarily, each project's
     registered watch paths (weaker signal — docs dirs only; keep so early adopters without
     an ingest scope still resolve).
   - Containment = candidate path equals the cwd or is an ancestor of it.
   - Exactly one distinct project → `Resolved`; several distinct projects → `Ambiguous`
     with the sorted id list; none → `None`.

3. **Outcomes**
   - `Resolved` → carry the canonical id into the existing `Canonicalize`/access/registration
     chain unchanged. Access mode and the write-path registration guard are enforced exactly
     as for an explicit id — defaulting grants no access.
   - `None` → keep the refusal, extended: `invalid-params: project_id is required (no
     registered project's scope contains cwd <X>; pass projectId explicitly, or register this
     directory with memory_watch_add / settings ingest scope add)`.
   - `Ambiguous` → `invalid-params: projectId is ambiguous from cwd <X>: candidates <a, b>`.
     Never guess.

4. **No new settings surface in v1.** The default applies only when the argument is omitted
   or blank; an explicit projectId wins unconditionally. If a host ever needs to suppress
   defaulting, that is a follow-up (`settings access`-style switch), not speculative config.

5. **Envelope** — responses reached via the default carry the resolved canonical id the same
   as explicit calls; no shape change.

## Tests

- Resolver: exact match; ancestor containment (`/repo/docs` contains `/repo/docs/sub`);
  ambiguity across two projects; none; watch-only fallback; canonicalization applied once.
- ToolGate: blank id + Resolved → proceeds and enforces access on the resolved id;
  blank id + None → enriched refusal; blank id + Ambiguous → candidate list, no call;
  explicit id → resolver never consulted (prove with a throwing fake).
- End-to-end (existing tool test harness): memory_search with no projectId against a server
  whose cwd sits inside a registered scope returns that project's data.

## Out of scope

- Any change to tool parameter schemas (projectId stays optional-shaped as today — it is
  already `string?` into the gate).
- Cloud sync / shared-tier semantics (shared entries are never project-defaulted).
