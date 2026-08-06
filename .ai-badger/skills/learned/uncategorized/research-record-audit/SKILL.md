---
name: research-record-audit
description: Use when auditing a research record's factual accuracy, citation truth, or grade correctness.
---

# Research Record Audit

Adversarially verify a written record (research note, findings doc, review write-up,
incident report, changelog) against its cited primary sources. Never trust the record:
**re-derive every load-bearing claim from the sources**. The record's author already
re-read their own reasoning — the errors live in the sources and in the summary layer,
not in re-reading the prose twice.

## When to use
- "Adversarially review the research record at <path> for factual accuracy and honesty"
- Any request to check a findings document's claims, grades, or open questions against
  the files/DBs/commands it cites.
- Delegated review of a research record for "citation truth, grade correctness,
  completeness" — the parent expects a verdict (APPROVE / APPROVE-WITH-CHANGES / REJECT)
  plus numbered findings tagged MUST-FIX / SHOULD-FIX / NIT with file:line on anything
  wrong. That shape is per-request; the ACCURATE/CORRECTED/OVERCLAIMED shape below is the
  other common one — ask which, or use the one the parent names.
- Verifying a session's or subagent's written work before it ships (quality gate).

## Workflow

1. **Index the record.** Read it fully. Extract per finding: the claim, its grade
   (MEASURED/READ/INFERRED/UNVERIFIED), its evidence citation (path:line), and any
   parentheticals/aggregate numbers. Also index the 'Still open'/open-questions section.
2. **Verify quotes verbatim.** For every quoted string, open the cited file at the cited
   line and compare word-for-word. Fair elisions (…) are OK; changed wording is not.
3. **Re-run every MEASURED claim** with the exact command or a read-only equivalent.
   Time-varying numbers (session counts, token totals) will drift between runs — check
   that the record hedged ("count grows as sessions run") before calling it wrong.
4. **Verify READ claims by reading the code/config**, not the record's paraphrase.
5. **Verify negative claims** (absence: "no dir for X", "0 archived rows"). Absence is
   only meaningful after ruling out the things that would explain it (prune policy,
   retention window, auto-delete) — check those before accepting "never existed".
6. **Audit the grades.** MEASURED must map to a rerunnable command you actually ran.
   INFERRED must be labeled INFERRED and hedged (it is reasoning, not observation).
   UNVERIFIED must be said plainly — never promote a guess to measured.
7. **Audit open questions** for unsupported parentheticals (see Pitfalls).
8. **Check call-path claims at the call site, not just the callee.** If a finding says the
   runtime calls X "before each API call" / "every turn", `search_files` for the method name
   and read the caller: gates (`is_trivial_prompt()`, scaffolding strips) can skip the call
   on some turns, and a record citing only the callee won't show them. Grep call sites also
   to separate code-truth from runtime-truth — a method contract can be accurate while the
   branch is dormant in production (exercised only by tests).
9. **Write the report** in the user's shape (below). Read-only throughout — never edit
   the record; open DBs with `mode=ro`.

## Report shape (user's required format)
- Verdict per finding: **ACCURATE / CORRECTED (with the correction) / OVERCLAIMED**
  (record says more than source supports). CORRECTED also covers understatements —
  any mismatch between record and source.
- **Grading errors**: list any MEASURED/READ/INFERRED/UNVERIFIED misgrades.
- **Honesty problems in 'Still open'**: check each open question's supporting claims.
- **Final recommendation: APPROVE or REVISE** with the specific edits, enumerated.
- **Cite path:line or the exact command for every verdict.** No uncited verdicts.

## Verification toolbox
- Read-only sqlite (never mutate the DB):
  `sqlite3.connect(f'file:{path}?mode=ro', uri=True)` — batch all SELECTs for a finding
  into one heredoc script so each query is attributable in the report. `PRAGMA table_info`
  for schema claims.
- Byte-identity: `diff -rq` / `diff` → "IDENTICAL" (check the record's claim literally).
- Quotes: `grep -n "phrase" file` to confirm the line exists; `read_file` with offset to
  read the actual cited range.
- Negative existence: `ls ~/.claude/projects | grep -i raccon` (exit 1 = absent) —
  cite the command's exit code in the report.
- CLI-surface claims: `hermes sessions --help` etc. — help text is the contract.
- Call-site/caller search: `search_files(pattern=..., path=...)` for the method name, then
  `read_file` at the hit's offset. For N locations, batch parallel `read_file` calls instead
  of a shell for-loop (long inline loops can trip the command parser).
- `read_file` reports "binary" on a .md/.py that isn't: UTF-8 text with very long lines is
  sometimes misdetected — confirm with `file`, then `grep -n` / `sed -n` still work.
- ABC surface dumps: `inspect.getmembers(ABC)` + `ABC.__abstractmethods__` to independently
  verify "N public members, M abstract" claims attached to a MEASURED finding.
- "Key exists anywhere in JSON" claims: depth-limited recursive walker (dict/list) —
  verify against the real files, don't assume.
- `hermes insights --days N` → Platforms table has a `subagent` row (per-dispatch spend
  aggregation). Numbers differ run-to-run; treat as snapshot.

## Pitfalls (learned the hard way)
- **Quotes survive; summary parentheticals drift.** In the motivating audit, every quoted
  line was verbatim-correct while the record's parenthetical "(subagentTokens 0 except
  one manual 85000 entry)" was wrong — the file had TWO manual entries (85000 + 90000,
  `subagentTokens: 175000`). Always re-check aggregate numbers and parentheticals, even
  when every quote checks out.
- **"File X writes Y" needs ALL writers.** "current-session.json, a file only
  session_start_hook.py writes" was imprecise — `user_prompt_hook.py` also refreshes it,
  both via the same shared helper. Check every writer, then judge whether the substance
  survives (it did: both writers are Claude-only hooks).
- **Docs can be aspirational; verify against mechanism.** An extension doc claimed scripts
  "are NOT scaffolded when hermes is in the agent list" — but the scaffolder has no
  agent-based script filtering at all (scripts dir copied wholesale; `diff -rq` identical).
  The doc was wrong, not the scaffold. Confirm the mechanism before blaming either side.
- **Absence ≠ never-existed.** Missing session ids were only provable because
  `auto_prune: False`, `retention_days: 90`, and 0 archived rows ruled out deletion —
  and the ids didn't even match the id shape. Chain the evidence; don't assert from one
  empty SELECT.
- **Don't fabricate verification.** If a number can't be reproduced (historical snapshot,
  deleted file), say "unverified — current evidence contradicts it" rather than letting
  the record's claim stand or inventing confirmation.
- **Hermes source is not in the project repo.** `hermes_cli/*`, `hermes_state.py`,
  `gateway/*` live in `~/.hermes/hermes-agent/`. Locate before citing. See
  `references/hermes-internals.md` for the map.
- **"Runs before each API call" ≠ runs on every turn.** Caller-side gates (trivial-prompt
  filters, skill-scaffolding strips) skip the call outright on some turns. The callee's own
  docstring says "Pre-turn" and the record repeats it — only the call site shows the gate.
- **Overview sentences overstate while the enumeration is accurate.** "The runtime calls the
  provider at five points in every agent turn" was loose (tool injection is init-time, tool
  dispatch conditional) though all five listed points were real and correctly cited. Check
  the header against its own bullet list.
- **Glosses on measured findings drift per item.** The measured output was verbatim-correct;
  the interpretive clause covering all seven providers ("they need external credentials")
  fit only five — two gate on CLI presence / local runtime instead.
- **Dormant branches read as runtime behavior.** "add_provider always admits the built-in
  provider, registered first" was true of the method, but the only production call site
  passes the external provider; the builtin branches run only in tests. Say which level a
  claim describes (code contract vs runtime path).
- **Cited ranges: check the endpoints.** Every Evidence path:line range must exist in the
  file AND contain the thing claimed — verify the tail line, not just the head.

## Support files
- `references/hermes-internals.md` — verified map of Hermes Agent internals: source
  locations, state.db schema, hook payloads, usage-file writer, request-dump semantics,
  and ai-badger task-tracking internals (as re-derived 2026-08-02).
- `references/evidence-record-citation-audit.md` — worked example: adversarial citation/
  grade audit of a Hermes memory-provider interface record (2026-08-06), with the
  call-site-gating catch and the code-truth vs runtime-truth distinction.
