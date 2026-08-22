# 0089. The project id is a registered guidv7 — accident prevention, not access control

Date: 2026-08-22

Status: Proposed — the owner ratifies this one; ratification flips it to Accepted. (No prior ADR
in this repo has used `Proposed`; the nearest shape is ADR-0012's
"Accepted (decision to replace; implementation … is a separate work item)".)

Plan: `docs/work/2026-08-22-post-delta-next-steps-plan.md` (rev 4, §S2), owner gate G2
(`docs/work/2026-08-22-delta-open-items-feedback.md` §G2, binding design input), review round on
PR #448 (two owner comments, folded — see the bracketed notes on decisions 1/3/4/5/6/7), ledger
`docs/work/2026-08-22-post-delta-continuation-plan.md` (S2).

## Context

A project id is whatever string a caller types. `ToolGate.RequireAsync` rejects a blank one and
nothing else (`src/AiRaccoon/Tools/ToolGate.cs:29-32`), and no path normalizes or validates it —
a repo-wide search for a trim/lowercase/normalize on `projectId` returns nothing. So the ids in
use are short human names (`jsaa`), which an agent can guess, mistype, or hallucinate, and which
another project's agent can reach by simply naming them.

Worse, **a project exists the moment something is written under its id**. There is no record of
which projects exist; the list is derived from the rows
(`MemorySql.SelectProjectIds`, `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:58-64`). So any
string at all is a valid project, and a typo does not fail — it founds a new one.

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

1. **The project id is a guidv7 that has been registered. The "token" and the id are the same
   string** — there is no second credential, no signature, no ACL. An id nobody can guess, plus a
   registry that says which ids are real, is the whole mechanism. The shape is already this
   repo's: `Guid.CreateVersion7().ToString(...)` (`src/AiRaccoon/Tools/MemoryTools.cs:186`,
   `src/AiRaccoon.Infrastructure/Workspace/WorkspaceService.cs:19`). Sortable is the reason for v7
   over v4: ids list in creation order for free.
   *[Was: "The project id becomes a guidv7 … an id nobody can guess is the whole mechanism," with
   no registry — a project still existed as soon as something was written under its id. Changed in
   review: the owner ruled that any valid guid creating a project by accident is itself the defect
   to remove.]*

2. **Ids are canonicalized at the tool boundary — the one piece of validation this adds.** Input is
   accepted in any form `Guid.TryParse` accepts and stored/compared as the lowercase `D` form
   (`8-4-4-4-12`). Without this, two spellings of one guid are two different projects: `project_id`
   and the vec0 `ctx` column are compared as strings, never as guids.

3. **A project exists when it is registered, not when it is first written to. A write to an id
   with no registry row is refused — guid or not.** The single exception is compatibility: a
   raw-text id the bank *already holds rows for* keeps working with a warning, exactly as the
   owner ruled at G2. So the refusal test is "no registry row **and** no existing rows", and
   `SelectProjectIds` (`MemorySql.cs:58-64`) is what answers the second half — see
   "Membership, and what the registry is not" below. This is what removes the accident the owner
   named: a random or mistyped guid is not a new project, it is a refusal.
   *[Was: "a non-guid id the bank does not know is refused" — guid-shaped ids still auto-created a
   project. Changed in review: the refusal is now about registration, not about the string's shape,
   which also retires my previous flag that this rule extended the owner's words.]*

4. **`project_id_token_get` mints *and registers*.** It generates a guidv7, inserts the registry
   row (with an optional `name`), and returns the id — so the tool call is the act that brings a
   project into existence. It looks nothing up; there is no id-to-project lookup to hole through.
   The owner named it `get-project-id-token`; the rename is convention only (family first, verb
   last, as `code_get` established against the `memory_*` family).
   *[Was: "mints and returns a fresh guidv7 and touches no rows … a project exists once something
   is written under its id." Changed in review.]*

5. **The registry table — proposed shape, and it is a proposal, not a settled schema.**

   ```sql
   CREATE TABLE IF NOT EXISTS projects (
       id         TEXT PRIMARY KEY,   -- canonical lowercase D-form guidv7 (decision 2)
       name       TEXT,               -- optional, human-facing; not unique, never an identifier
       created_at INTEGER NOT NULL
   );
   ```

   `name` is nullable and carries no meaning to any query — it exists so a human reading
   `memory_stats` or `access list` sees something other than 36 hex digits. It is deliberately
   **not** unique and **never** accepted where an id is expected: the moment a name can be used to
   address a project, the guessability this ADR removed comes straight back.

   **No `CurrentVersion` bump.** The table belongs in the unconditional `Ddl` block, the shape the
   `metrics` table established and the code corpus reused — "In Ddl (not the `if (fresh)` branch)
   so it and both indexes reach legacy banks on the next open, not only fresh ones"
   (`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:344-346`, and `:414` for the code tables'
   "no `CurrentVersion` bump (metrics-table precedent above)"). ADR-0086 records why this matters
   and is not a style preference: `user_version` survives `VACUUM INTO` and the sync gate refuses a
   pull from a newer `user_version`, so a bump hard-fails every concurrent session and every peer.
   `CurrentVersion` stays 10 (`MemorySchema.cs:54`).
   *[New in review — there was no table in the first draft.]*

6. **Two CLI verbs under a new top-level `project` family**: `project id generate` (mint, register,
   print — the CLI twin of decision 4, with an optional `--name`) and `project id convert <old-id>`
   (rewrite an existing raw-text project to a guid, one transaction, **one way** — there is no
   guid-to-text verb, because reversing restores the guessable name). **`convert` registers the new
   id with `name` = the old raw id by default**, overridable with `--name`, so the human label that
   was the id survives as a label. Placement: ADR-0076's rule is that a settings-backed *subsystem*
   is a node under `settings` (`src/AiRaccoon/Setup/Cli/CliCommandTree.cs:10-12`); these are
   operations, and the `settings` node's own help says operations live at the top level beside
   `watch registered`, `extract prune`, `model set` (`CliCommandTree.cs:83`).
   *[Was: `generate` minted without registering, and `convert` said nothing about a name. Changed in
   review.]*

7. **`convert` must re-derive the stored `ctx` values, not only `project_id`.** The vec0 `ctx`
   column is written once, by trigger, at insert time — `MemorySql.ContextKeyExpression`
   (`MemorySql.cs:705-714`) for the memory corpus (`MemorySchema.cs:158,195`) and `NEW.project_id`
   verbatim for the code corpus (`MemorySchema.cs:487`). Those triggers fire on the embedding
   columns, not on `project_id`, so a plain `UPDATE entries SET project_id = …` leaves every vector
   row partitioned under the old string and the converted project's corpus becomes unreachable.
   Settings keys are in the same position: `access.mode.project:<id>` is built from the id
   (`src/AiRaccoon.Core/Access/AccessModePolicy.cs:13`).

8. **The token file is kept out of memory by `ai-raccoon.ignore` — it is *not* git-ignored.** The
   id lives in a file in the project's tree, and the verb that writes it appends that path to the
   tree's `ai-raccoon.ignore` (ADR-0086 decision 5) when it is not already matched. The file is a
   plain line-per-pattern text file with `#` comments inert
   (`src/AiRaccoon.Core/Watch/IgnoreRules.cs:36-45`), read fresh on every call with no cache
   (`src/AiRaccoon.Infrastructure/Ingestion/IgnoreRulesProvider.cs:5-8,20-30`), so appending a line
   is safe; nothing in `src/` writes it today, so the append is new behaviour.

   **Why the ignore file rather than `.gitignore`:** the concern is the id reaching the memory
   bank, not reaching git. Two mechanisms exist and only one of them covers the whole surface. A
   directory walk already skips a hidden ancestor segment, so a token under a dot-directory is
   never picked up by a scan (`src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:140,410` →
   `IngestPath.HasHiddenOrDeniedSegment`). An **explicit `memory_ingest_file` of that same path is
   not covered**: the single-file guard tests only the leaf filename for a leading dot
   (`FileIngestor.cs:47,398-401`), so `.ai-raccoon/project-id` passes it. The `ai-raccoon.ignore`
   entry is what closes that path — ADR-0086 decision 5 makes ignore win over an explicit
   `memory_ingest_file`, and an ignored path is "never fingerprinted, never chunked" in either
   corpus (`FileIngestor.cs:52-57`). A `.gitignore` line would have closed neither.

   Two constraints the implementation inherits from ADR-0086: there is **one ignore file per
   watch/ingest root and no nested discovery** (`IgnoreRulesProvider.cs:5-8`), so the verb must
   write at the root that actually covers the token file; and **editing the ignore file triggers a
   full re-scan of the watch**, single-flighted — a real cost on a repo-root watch, paid once.
   *[Was: "the verb that writes it also ensures the path is git-ignored, adding the entry when it is
   not." Reversed by the owner in review — "Not gitignored - ai-raccoon.ignore - we dont want this
   file in memory". Recorded rather than silently swapped, because a reader who knows the first
   draft will otherwise look for a `.gitignore` write that no longer exists.]*

9. **No global mode, and O6 is unchanged by this ADR.** `memory_promotion_list` with
   `allProjects=true` still runs no access check (`src/AiRaccoon/Tools/PromotionTools.cs:39-47`) —
   verified at HEAD, unchanged. That branch was built without a "global mode" so as not to pre-empt
   this design; this design declines to introduce one, because a real global mode is access control
   and access control is outside the threat model. O6's shape is revisited only when an ADR lands
   caller identity — not by this one.

## Membership, and what the registry is not

The one real design question the registry raises: does "belongs to this project" now mean "has a
`projects` row" instead of "has entries rows"? **No. The two answer different questions and both
stay.**

- **ADR-0046's `ProjectRows` is untouched.** It is a *row-level* predicate — a project's rows are
  its committed rows plus any context-labelled rows inside it — and ADR-0046 exists precisely
  because that rule had been hand-copied into nine queries, a trigger and a search filter until
  they disagreed. **Nothing in this ADR adds a `projects` join to any of those sites.** Doing so
  would rebuild the same hand-maintained mirror ADR-0046 tore out, and
  `ProjectRowsSingleDefinitionTests` is the gate that would have to be argued with.
- **The registry answers a different question: which projects *exist*.** Today that is derived from
  the rows (`SelectProjectIds`), which is exactly why a project with no writes is invisible and why
  any string is a project. After this ADR, existence is a fact of record.
- **So `SelectProjectIds` narrows in meaning to "projects that have content"**, and becomes a
  strict subset of the registry — a registered project that has not been written to yet has no
  entries rows. Listing surfaces meant to show *projects* read the registry; anything asking "which
  projects have rows" keeps `SelectProjectIds` unchanged.
- **`SelectProjectIds` also gains one new job**: it is the legacy oracle for decision 3's refusal —
  the "does the bank already hold rows under this raw-text id" half of the test. That is the whole
  of the compatibility path, and it is why the two definitions have to keep coexisting rather than
  one replacing the other.

**What the registry is not:** it is a directory, not a gate. Any caller through `/mcp` can read it
and list it; registering a project grants nothing and proves nothing about who is calling. It
prevents an accident — a typo'd or random guid now fails loudly instead of founding a ghost
project — and that is its entire claim. Access control it is not, before or after.

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
- The bank lists its own project ids on demand (`MemorySql.cs:58-64`) — and after this ADR the
  registry lists them too, names included. **The registry makes enumeration easier, not harder.**
  That is the honest cost of decision 5 and it is accepted: hiding the list from a caller that
  already holds the bank's token would be theatre.

**What the guid plus the registry changes:** guessing stops working, and so does stumbling. An
agent that types `jsaa` when it meant `jsaa-2` no longer lands in a real project; an agent that
invents a well-formed guid no longer creates one. The accident is what gets prevented.

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
   (`MemorySchema.cs:479-481,487`). A guid flows into `ctx` as the same opaque string, longer and
   unguessable; the memory corpus keeps composing its key through `ContextKeyExpression`, whose
   length-prefixed form already tolerates any id content (`MemorySql.cs:705-714`). Rewriting those
   stored values is decision 7's job — this ADR forbids changing the expression, and the `projects`
   table is not joined into the vector path at all.
   *(Correction to the S2 brief: the plan says `vec_code` partitions on a `ctx` derived from
   `ContextKeyExpression`. It does not — `vec_code` stores the raw `project_id`; only
   `vec_entries`/`vec_structure` use the expression.)*
3. **The `search_quality` exclusion's reasoning is unchanged.** ADR-0088 §8 excludes `kind=code`
   and `kind=both` from recording because the rows sync off-machine and would carry source
   identifiers and paths (`src/AiRaccoon.Core/Memory/SearchDispatcher.cs:44-48`). The table stores
   `project_id` (`MemorySchema.cs:327`) and `top_source_files` (`MemorySchema.cs:330`) — the leak
   the exclusion prevents is the query text and the file paths, never the project name, so a guid
   neither weakens nor removes the reason to exclude. It does make the `project_id` column itself
   non-identifying, which is a small side benefit and not retroactive. One new wrinkle from decision
   5: `projects.name` is a human label that may say more than a guid does, so **if the registry ever
   syncs, the name is the column to strip** — flagged here, decided by whoever scopes sync.
4. **O6 / `allProjects=true`** — see decision 9: no global mode is introduced, the branch keeps its
   shape, and its revisit stays tied to a future access-control decision.

## Consequences

- **Positive**: the two common accidents — naming another project, and founding a project by typo —
  both stop being one keystroke away, at the cost of one table, one id format and one
  canonicalization step. No new credential to store, rotate, or lose beyond the id itself.
- **Positive**: `projects.name` gives back the readability the guid takes away, without making the
  name addressable.
- **Positive**: `convert` is the first time the id lives in exactly one derivation. Decision 7 makes
  the `ctx` rewrite part of the decision instead of a defect discovered after the first conversion
  silently orphaned a corpus.
- **Negative**: registration is a new step before the first write, and it is a breaking change for
  any workflow that creates a project by writing to a new name. It is the point, but it is a break —
  and the failure lands on `memory_write`, which is where it will be met first.
- **Negative**: two definitions of "project" now coexist — the registry and `SelectProjectIds` — and
  they can disagree (a registered project with no rows; a legacy id with rows and no registration).
  Both disagreements are intended and enumerated above, but a reader who finds only one of them will
  read it as drift.
- **Negative**: the registry makes the project list easier to read, for every caller, including the
  ones this ADR is trying to keep out by accident. Accepted, and stated in the enumeration answer.
- **Negative**: the token file is ignored for *memory*, not for *git* — it will be committed unless
  the human decides otherwise. That is the owner's ruling and the right one for the stated concern,
  but it means the id is as shared as the repo is.
- **Negative**: appending to `ai-raccoon.ignore` triggers a full re-scan of the covering watch
  (ADR-0086 decision 5). One-off, but not free on a repo-root watch.
- **Neutral**: existing banks are untouched until someone runs `convert`; warn-but-work means no
  forced migration, and the `Ddl` placement means the table arrives on the next open with no version
  bump and no sync cliff.
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
- **Whether the registry ever syncs**, and if it does, whether `projects.name` is stripped the way
  ADR-0085's never-syncs rule strips code identifiers. Flagged in constraint 3; not decided here.
- **What happens to a registry row when a project's last row is deleted.** The row survives — a
  project may be empty — but no reaping, archiving or `project id remove` verb is specified.
- **Whether `allProjects=true`, `memory_sync`'s whole-bank upload, or `memory_write` to `shared`
  should change** (ADR-0051's open list). They stay as they are.
- **The implementation's PR split, migration ordering, and which surfaces emit the warning.** S2 is
  the design; the implementation is scoped separately.

Extends ADR-0046 (whose single definition of *membership* it deliberately leaves alone) and answers
the first bullet of ADR-0051's "What this does not fix" only in part — a caller may still name any
registered project id it likes; it can no longer guess one or invent one. Depends on ADR-0086 (the
`ai-raccoon.ignore` file and its one-root/re-scan semantics) and follows its no-version-bump
precedent for the new table. Constrained by ADR-0085/ADR-0088 (the code corpus and its search
surface) and ADR-0076 (where a new CLI node belongs).
