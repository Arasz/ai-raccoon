# Repair fragmented project ids

Fold several project ids that name the same project into one canonical id, so
memory written under `job-search-ai-assistant` and `jsaa` (for example) shows up
under a single id instead of splitting across two.

## When you need this

You will see fragmented ids if you renamed a project directory, moved a repo, or
ever called `memory_write`/`memory_search` with an inconsistent id for the same
project. Each variant is a separate committed context (`project:<id>`), so
`memory_search` against the "wrong" one silently misses the entries filed under
the other. The public binary ships with no built-in id folds
([ADR-0099](../adr/0099-empty-default-alias-map.md)); every fold has to be
declared on purpose, so it never merges two ids that only look alike.

## Step 1: See what the bank actually has

```bash
ai-raccoon repair project-ids
```

This diagnoses only, nothing is queued or changed. It censuses every project id
the bank knows about and buckets each one:

- **fold**: matches a canonical id under an alias map you supplied (none yet on a
  first run).
- **drop**: test residue (empty, unregistered, no entries).
- **retire**: registered in the projects table but owns nothing anymore.
- **needs a human to attribute**: doesn't match anything, so you decide what it is.
- **pinned**: would fold, but something is holding it back (reason printed inline).
- **needs nothing**: already correct or genuinely empty.

The first run also writes an editable alias-map template next to the bank
(`<data-root>/project-id-map.template.json`), pre-filled with the bank's
registered ids as canonicals and its unresolved ids parked under `Dropped` for
you to move into `Aliases` if they are really the same project. **Never run
`--apply` against a template you haven't reviewed.** The whole point is that no
fold happens until a person confirms it.

## Step 2: Edit the alias map

Open the template and move any id that is genuinely a duplicate from `Dropped`
into `Aliases`, naming the old id and the canonical one it should fold into:

```json
{
  "Aliases": [{ "Alias": "old-project-id", "Canonical": "new-project-id" }],
  "Canonicals": ["__self_metrics__", "new-project-id"],
  "Dropped": ["some-test-residue-id"]
}
```

Leave genuine test residue and anything you don't recognize in `Dropped`. An id
left there is deleted (with a tombstone per removed hash) rather than folded.

## Step 3: Run it for real

```bash
ai-raccoon repair project-ids --apply --map ./project-id-map.template.json
```

Plain `--apply` runs a **run-until-fixed loop**: it commits a fold request, waits
one server maintenance poll (about 15 seconds), re-censuses, and repeats, until
the plan converges, only pinned ids remain, or it gives up. Each pass prints
what moved. Add `--queue-only` instead if you are scripting this and would
rather queue one request and exit immediately, without waiting for convergence
yourself.

A fold is single-pass: if something is still writing under the losing id while
the fold runs, that id can reappear. Quiesce writers under the id you're folding
before running this, or just re-run the command. It re-censuses every time, and
folding is safe to repeat.

## Reading the outcome

The last line is always one of:

| Outcome | Meaning |
|---|---|
| `converged` | Nothing left to fold, drop, or retire. |
| `pinned-only` | Everything actionable is held back for a stated reason (see the reasons printed above it). |
| `repair needed` | Re-run with `--apply` to act on the plan just shown. |
| `attention needed` | Settled, but some ids still need a human to attribute in the alias map. |
| `stuck` | The same actionable set didn't move across two passes; quiesce writers and check the server log. |
| `writers-active` | Bank totals grew while the loop ran; something is actively writing under a folded id. |

Exit codes: `0` on `converged`/`pinned-only`/queued success, `15`
(`Usage.AliasMapInvalid`) if `--map` names a file that doesn't parse, `37`
(`Bank.RepairStuck`), `38` (`Bank.RepairWritersActive`), and `39`
(`Bank.RepairAttentionNeeded`). See
[Configure and run the AiRaccoon server](configure-ai-raccoon-server.md) for the
full exit-code table shared by every command.

## What this does not do

It never guesses which ids are duplicates. That judgment call is what the
alias map is for. It also only folds the committed `project` and `custom`
scopes ([ADR-0100](../adr/0100-repair-folds-all-committed-scopes.md)). The
`shared` tier is untouched, and workspace rows are per-agent scratch space that
this repair never reaches at all.

## See also

- [ADR-0099: the public binary ships an empty project-id alias map](../adr/0099-empty-default-alias-map.md)
- [ADR-0100: the project-ids repair folds every committed scope](../adr/0100-repair-folds-all-committed-scopes.md)
- [`repair` command reference](../reference/cli-reference.md)
