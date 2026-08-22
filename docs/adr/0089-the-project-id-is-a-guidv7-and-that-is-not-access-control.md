# 0089. The project id is a guidv7 — accident prevention, not access control

Date: 2026-08-22

Status: Proposed — the owner ratifies this one; ratification flips it to Accepted. (No prior ADR
in this repo has used `Proposed`; the nearest shape is ADR-0012's
"Accepted (decision to replace; implementation … is a separate work item)".)

Plan: `docs/work/2026-08-22-post-delta-next-steps-plan.md` (rev 4, §S2), owner gate G2
(`docs/work/2026-08-22-delta-open-items-feedback.md` §G2, binding design input), ledger
`docs/work/2026-08-22-post-delta-continuation-plan.md` (S2).

## Context

A project id is whatever string a caller types. `ToolGate.RequireAsync` rejects a blank one and
nothing else (`src/AiRaccoon/Tools/ToolGate.cs:29-32`), and no path normalizes or validates it —
a repo-wide search for a trim/lowercase/normalize on `projectId` returns nothing. So the ids in
use are short human names (`jsaa`), which an agent can guess, mistype, or hallucinate, and which
another project's agent can reach by simply naming them.

There is no caller identity anywhere below that. ADR-0051's "What this does not fix" already says
so: access mode "resolves the mode of the project the caller *names*; there is no caller identity
anywhere in `IMemoryAccessGuard`". The credential that does exist authenticates the **bank**, not
the project: one `mcp-token` per data root (`src/AiRaccoon/Hosting/Common/McpTokenFile.cs:15,38-45`),
minted at server start, `0600` on POSIX only — on Windows the file inherits the data-root ACL, an
ADR-0020 non-goal (`McpTokenFile.cs:167-171`). ADR-0020's own non-goals put it plainly: "the token
proves the caller can read a file in the data root; every holder still gets the full tool surface"
(`docs/adr/0020-always-on-http-stdio-proxy.md:227-229`), restated in
`docs/adr/0022-authenticated-loopback-restart.md:176-177`. **Project isolation today is a naming
convention over a shared credential.**

The owner's decision at gate G2 is to raise the cost of the *accident* without pretending to build
a boundary: other projects on the same machine stay trusted, and an attacker willing to spend
effort is out of scope.

## Decision

1. **The project id becomes a guidv7. The "token" and the id are the same string** — there is no
   second credential, no registry, no signature, no ACL. An id nobody can guess is the whole
   mechanism. The shape is already this repo's: `Guid.CreateVersion7().ToString(...)`
   (`src/AiRaccoon/Tools/MemoryTools.cs:186`,
   `src/AiRaccoon.Infrastructure/Workspace/WorkspaceService.cs:19`). Sortable is the reason for
   v7 over v4: `MemorySql.SelectProjectIds` (`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:58-64`)
   then lists projects in creation order for free.

2. **Ids are canonicalized at the tool boundary — the one piece of validation this adds.** Input is
   accepted in any form `Guid.TryParse` accepts and stored/compared as the lowercase `D` form
   (`8-4-4-4-12`). Without this, two spellings of one guid are two different projects: `project_id`
   and the vec0 `ctx` column are compared as strings, never as guids.

3. **A non-guid id the bank already knows keeps working, with a warning; a non-guid id the bank
   does not know is refused.** This is the owner's two sentences — "new projects need to provide
   guid id, old ones can use old id (we can add a warning)" — and "does the bank already hold rows
   for this id" is the cheap test that separates them (`MemorySql.SelectProjectIds`, the same
   definition ADR-0046 made single). The refusal is the only place the guid actually stops an
   accident on the **write** path: a mistyped or hallucinated name fails loudly instead of silently
   founding a ghost project. Creating a project therefore becomes a deliberate act — generate an id
   first. Recorded here because a reader will otherwise take the first failing `memory_write` for a
   defect (`.ai-badger/invariants/record-deliberate-design-choices.md`).

4. **One new MCP tool, `project_id_token_get`: it mints and returns a fresh guidv7 and touches no
   rows.** It looks nothing up — there is no id-to-project lookup to hole through — and a project
   exists once something is written under its id. The owner named it `get-project-id-token`; the
   rename is convention only (family first, verb last, as `code_get` established against the
   `memory_*` family).

5. **Two CLI verbs under a new top-level `project` family**: `project id generate` (mint, print,
   touch nothing) and `project id convert <old-id>` (rewrite an existing raw-text project to a
   guid, one transaction, **one way** — there is no guid-to-text verb, because reversing restores
   the guessable name). Placement: ADR-0076's rule is that a settings-backed *subsystem* is a node
   under `settings` (`src/AiRaccoon/Setup/Cli/CliCommandTree.cs:10-12`); these are operations, and
   the `settings` node's own help says operations live at the top level beside `watch registered`,
   `extract prune`, `model set` (`CliCommandTree.cs:83`).

6. **`convert` must re-derive the stored `ctx` values, not only `project_id`.** The vec0 `ctx`
   column is written once, by trigger, at insert time — `MemorySql.ContextKeyExpression`
   (`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:705-714`) for the memory corpus
   (`MemorySchema.cs:158,195`) and `NEW.project_id` verbatim for the code corpus
   (`MemorySchema.cs:487`). Those triggers fire on the embedding columns, not on `project_id`, so a
   plain `UPDATE entries SET project_id = …` leaves every vector row partitioned under the old
   string and the converted project's corpus becomes unreachable. Settings keys are in the same
   position: `access.mode.project:<id>` is built from the id
   (`src/AiRaccoon.Core/Access/AccessModePolicy.cs:13`).

7. **The token is file-stored, and the file is auto-ignored.** The id lives in the project's own
   tree; the verb that writes it also ensures the path is git-ignored, adding the entry when it is
   not. No code writes `.gitignore` today (a repo-wide search finds no writer), so this is new
   behaviour, not a reuse. Storage instructions ship with the verbs and the tool description.

8. **No global mode, and O6 is unchanged by this ADR.** `memory_promotion_list` with
   `allProjects=true` still runs no access check (`src/AiRaccoon/Tools/PromotionTools.cs:39-47`) —
   verified at HEAD, unchanged. That branch was built without a "global mode" so as not to pre-empt
   this design; this design declines to introduce one, because a real global mode is access
   control and access control is outside the threat model. O6's shape is revisited only when an
   ADR lands caller identity — not by this one.

## Can each project still enumerate the DB while the bank is unencrypted?

**Yes — before and after this ADR, and encrypting the bank does not change the answer.** The
mechanism, in four steps:

- Anything that can read `<data-root>/mcp-token` gets the full tool surface
  (`McpTokenFile.cs:15,38-45`; ADR-0020:227-229; ADR-0022:176-177). One token per bank, not per
  project.
- The gate that follows resolves the mode of the project the caller *names*, and **reads are
  allowed in every mode with the settings lookup skipped entirely**
  (`src/AiRaccoon/Access/MemoryAccessGuard.cs:24-28`;
  `src/AiRaccoon.Core/Access/AccessModePolicy.cs:20` — `AccessRequirement.Read => true`).
- `memory_promotion_list` with `allProjects=true` skips the gate outright and returns every
  project's queued content (`PromotionTools.cs:39-47`; already recorded in ADR-0051's "What this
  does not fix", alongside `memory_sync` uploading the whole bank).
- The bank lists its own project ids on demand (`MemorySql.cs:58-64`).

**What the guid changes:** guessing stops working. An agent that types `jsaa` when it meant
`jsaa-2` no longer lands in a real project — an unknown guid is an empty project, and under
decision 3 a *new* non-guid id is refused rather than quietly created. The accident is what gets
prevented.

**What it does not change:** every enumeration path above still returns everything, because the
ids are rows in the bank, not secrets kept from it. **Encryption-at-rest is orthogonal**: it
decides who can open the file, not what a caller reaching a running server through `/mcp` can see —
the server holds the key and serves from the decrypted store. A guid raises the bar from "type the
name" to "read one row of the bank you already have a token for". That is a bar, not a boundary —
the same words ADR-0020 used for the token itself.

## The four scope constraints

1. **The `mcp-token` authenticates the bank, not the project** — stated plainly in Context above and
   unchanged by this ADR. Per data root, minted at server start, `0600` on POSIX, Windows inherits
   the data-root ACL as an ADR-0020 non-goal (`McpTokenFile.cs:15,38-45,167-171`).
2. **Per-project vec0 partitioning is preserved** — the `ctx` mechanism does not change, only the
   value it carries. `vec_code` is declared `vec0(ctx TEXT, embedding float[768] …)` and its trigger
   inserts `NEW.project_id` as `ctx` directly, because code is project-scoped only and never needs
   `ContextKeyExpression`'s shared/workspace/custom branching
   (`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:479-481,487`). A guid flows into `ctx` as
   the same opaque string, longer and unguessable; the memory corpus keeps composing its key through
   `ContextKeyExpression`, whose length-prefixed form already tolerates any id content
   (`MemorySql.cs:705-714`). Rewriting those stored values is decision 6's job — this ADR forbids
   changing the expression.
   *(Correction to the S2 brief: the plan says `vec_code` partitions on a `ctx` derived from
   `ContextKeyExpression`. It does not — `vec_code` stores the raw `project_id`; only
   `vec_entries`/`vec_structure` use the expression.)*
3. **The `search_quality` exclusion's reasoning is unchanged.** ADR-0088 §8 excludes `kind=code`
   and `kind=both` from recording because the rows sync off-machine and would carry source
   identifiers and paths (`src/AiRaccoon.Core/Memory/SearchDispatcher.cs:44-48`). The table stores
   `project_id` (`MemorySchema.cs:327`) and `top_source_files` (`MemorySchema.cs:330`) — the leak
   the exclusion prevents is the query text and the file paths, never the project name, so a guid
   neither weakens nor removes the reason to exclude. It does make the `project_id` column itself
   non-identifying, which is a small side benefit and not retroactive: rows already written under a
   raw-text id keep it, and `convert` (decision 6) is where they would be rewritten if the owner
   wants that.
4. **O6 / `allProjects=true`** — see decision 8: no global mode is introduced, the branch keeps its
   shape, and its revisit stays tied to a future access-control decision.

## Consequences

- **Positive**: the common accident — one agent naming another project — stops being one keystroke
  away, at the cost of one id format and one canonicalization step. No new credential to store,
  rotate, or lose beyond the id itself.
- **Positive**: `convert` is the first time the id lives in exactly one derivation. Decision 6 makes
  the `ctx` rewrite part of the decision instead of a defect discovered after the first conversion
  silently orphaned a corpus.
- **Negative**: the id file is git-ignored, so a fresh clone has no id and a lost file leaves rows
  nothing names. Recovery exists and is deliberately the same hole as the enumeration answer above:
  the bank can list its project ids. A design where the id were recoverable *only* by its owner
  would be access control.
- **Negative**: guids are unreadable. Every human-facing listing — `memory_stats`, `access list`,
  the promotion queue — gets less legible, and nothing here adds a label to compensate.
- **Negative**: refusing a non-guid new id (decision 3) is a breaking change for any workflow that
  creates a project by writing to a new name. It is the point, but it is a break.
- **Neutral**: existing banks are untouched until someone runs `convert`; warn-but-work means no
  forced migration.
- **Not addressed**: watch registrations, sync payloads and metrics rows all carry the project id
  and are converted by the same transaction — enumerating those tables is the implementation task's
  first job, not this ADR's.

## What this ADR does not decide

- **Key rotation.** The id never rotates. An id that leaks is an id that leaked; re-issuing one is a
  `convert`, and no revocation exists because nothing checks a revocation list.
- **Encryption-at-rest coupling.** Whether and how the bank is encrypted (ADR-0012 and successors)
  is orthogonal, as the answer above shows. This ADR neither requires encryption nor is weakened by
  its absence.
- **Any multi-user or remote-bank story.** Caller identity, per-project credentials, and a bank
  reachable from another machine are all out of scope; the threat model is one trusted user on one
  machine.
- **Whether `allProjects=true`, `memory_sync`'s whole-bank upload, or `memory_write` to `shared`
  should change** (ADR-0051's open list). They stay as they are.
- **The implementation's PR split, migration ordering, and which surfaces emit the warning.** S2 is
  the design; the implementation is scoped separately.

Extends ADR-0046 (one definition of "rows belonging to this project") and answers the first bullet
of ADR-0051's "What this does not fix" only in part — a caller may still name any project id it
likes; it can no longer guess one. Constrained by ADR-0085/ADR-0088 (the code corpus and its
search surface) and ADR-0076 (where a new CLI node belongs).
