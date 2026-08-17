# ADR-0013: Bill by job count, not runtime — collapse CI jobs and move the heavy gate local

**Date**: 2026-07-28
**Status**: Accepted
**Deciders**: Rafal Araszkiewicz
**Stakeholders**: Repository owner (billing), anyone editing `.github/workflows/`

---

## Context

This is a private repository, so Actions minutes are billed. On 2026-07-26 and 2026-07-27 every
workflow run on this account failed within roughly three seconds, with no step ever starting and
this annotation attached:

> The job was not started because recent account payments have failed or your spending limit needs
> to be increased. Please check the 'Billing & plans' section in your settings

95 runs died that way on 07-26 alone. The block lifted by 07-28. The same account-level block hit
`job-search-ai-assistant` on 2026-07-22 and produced that repo's ADR-0053, from which the patterns
below are ported. This decision is therefore preventive rather than a rescue — but the ceiling is
real, it has been hit, and a repeat blocks CI outright with no warning.

### What the minutes were actually spent on

Measured from the three pushes on 2026-07-28, the first runs to execute for real after the block
lifted. They carried a docs-and-config-only change, so nothing needed verifying:

| Job | Wall clock | Billed |
|---|---|---|
| `frontend` / `changes` | 4–7s | 1 min |
| `backend` / `changes` | 5–7s | 1 min |
| `infrastructure` / `changes` | 5–7s | 1 min |
| `performance` / `changes` | 5–8s | 1 min |
| `quality` / `shell scripts` | 5–7s | 1 min |
| `quality` / `workflow syntax` | 11–16s | 1 min |
| `quality` / `secret scan` | 7–13s | 1 min |
| `quality` / `dependency CVEs (frontend)` | 8–10s | 1 min |
| `quality` / `dependency CVEs (backend)` | 8–13s | 1 min |
| `Labeler` / `label` | 6s | 1 min |

**32 billed minutes across those three pushes.** Every job that verifies something —
`build-and-test`, `lighthouse`, infrastructure `test` — was skipped in all three.

The cause is not slow jobs. **GitHub bills a one-minute minimum per job**, so a workflow's job
*count* sets its floor price regardless of how fast each job is. Ten jobs averaging seven seconds
bill the same ten minutes as ten jobs averaging fifty seconds. This repo had spent its structure on
job granularity: four workflows each ran a dedicated `changes` job — a full runner, a
`fetch-depth: 0` clone, and one `git diff` — purely to decide whether the *next* job should run.
That is one billed minute per workflow per event to save a job that was often already cheap.

### The constraint that shapes everything else

Ruleset 19174527 requires four contexts: `build-and-test`, `secret scan`,
`dependency CVEs (backend)`, `dependency CVEs (frontend)`. These platform facts are load-bearing
and a future edit that ignores any of them reintroduces a real defect:

| Fact | Consequence |
|---|---|
| A job skipped by a **job-level `if:`**, in a workflow that ran, reports `skipped`, and GitHub counts `success`/`skipped`/`neutral` as satisfying a required check. | Expensive jobs can be gated off and their required context still goes green. This is what makes draft-PR gating safe on a required job. |
| A workflow skipped by **path/branch filtering** produces **no check run at all**. | The required context sits at "Expected — waiting for status" forever and the PR is permanently unmergeable. This is what issue #75 hit. |
| A job whose `needs:` dependency is **skipped** is itself skipped, unless its own `if:` contains `always()`/`!cancelled()`. | The old `needs: changes` shape meant a *failing* detector silently disabled the real job rather than failing loudly. |
| YAML treats a leading `!` as a tag indicator. | Any `if:` starting with `!cancelled()` needs a `>-` block scalar. |

So `paths:` filtering is free and correct on workflows owning no required check, and forbidden on
those that do. That single distinction determines which of the two fixes each workflow gets.

### A latent bug the measurement exposed

The detector computed its base as
`${{ github.event.pull_request.base.sha || github.event.before || 'HEAD~1' }}`. On a branch's first
push and after a force-push, `github.event.before` is not empty — it is
`0000000000000000000000000000000000000000`, which is truthy, so the `'HEAD~1'` fallback never
fired. `git diff 0000000… HEAD` then failed, failing the detector job, which skipped the real job
behind it. The gate **failed closed into a green PR**: a required check reporting `skipped`, with
no verification behind it and nothing red to notice.

## Decision

### 1. No job may exist solely to compute a changed-paths flag

Where the workflow owns **no** required check (`performance.yml`, `infrastructure.yml`), the
detector is replaced by a native `paths:` filter on the trigger. Trigger filtering costs nothing —
no runner is allocated at all.

Where the workflow **does** own a required check (`frontend.yml`, `backend.yml`), the trigger must
stay unfiltered so the context always reports, and the changed-paths test moves *into*
`build-and-test` as its first step, with the remaining steps guarded on its output. One billed
minute instead of two, and the job still reports on every PR.

### 2. The changed-paths test fails open

When the base commit cannot be resolved — all-zeros `before`, or a rewritten history — the step
runs the checks rather than skipping them. An unverified green is worse than a billed minute. This
is a deliberate inversion of the previous behaviour, which failed closed.

### 3. Draft PRs skip everything except secret scanning

This repo's own invariants mandate "small commits, early draft PR", so draft events are the
dominant case, not the exception. Every gate is now `if: github.event_name != 'pull_request' ||
github.event.pull_request.draft == false`. GitHub refuses to merge a draft, so the gate loses
nothing, and a skip satisfies the required contexts.

`ready_for_review` is added to every `pull_request` trigger's `types:`. Without it, marking a draft
ready would not re-run anything and the checks would stay skipped against code nobody verified —
which would turn this saving into exactly the silent-green failure mode §2 fixes.

`secret scan` is deliberately exempt and runs on drafts too. A secret is compromised the moment it
is pushed to the remote; deferring the scan until "ready" finds it after it already leaked.

### 4. Jobs that share a runner's setup cost get merged

`quality.yml`'s `shell scripts`, `workflow syntax` and the silenced-failure guard became one
`static checks` job. None of the three is a required context, so this needs no ruleset edit. Three
billed minutes became one for the same work.

The `dependency CVEs` matrix is **not** merged despite being the same shape: both matrix legs are
required contexts, and the check-run name *is* the required context string. Merging or renaming
them would strand every open PR behind a context that never reports again.

### 5. Concurrency and timeouts everywhere, with one deliberate exception

`quality.yml` had no `concurrency` group at all — five jobs ran to completion for every superseded
push. It now has one, keyed on event name as well as ref so the weekly cron and a `main` push
cannot evict each other.

`infrastructure.yml` deliberately keeps **no** workflow-level group. Its `apply` job serializes
production applies with `cancel-in-progress: false`, and a cancelling group at workflow level
would abort a `terraform apply` mid-flight against live remote state. Each of its jobs carries its
own group instead.

### 6. The heavy verification moves to a local pre-push hook

`lefthook.yml` runs the same lint/test/build that CI runs, before the push leaves the machine.
Measured at 79 seconds with everything changed; jobs whose glob does not match are skipped, so a
docs-only push exits in 0.00s.

It is bypassable by design — `--no-verify`, `LEFTHOOK=0`, `LEFTHOOK_EXCLUDE=<job>`. A hook that
cannot be escaped gets uninstalled, which is worse. The model depends on bypassing staying
exceptional, which is a people problem this ADR cannot settle.

### 7. The invariants are executable, not documented

`.github/tests/workflow-cost-invariants.py` runs inside `quality.yml`'s `static checks` and in the
pre-commit hook. It fails the build if an edit reintroduces a detector job, drops a concurrency
group, omits a `timeout-minutes`, or adds `paths:` to a workflow owning a required check. Prose in
this file does not survive a hurried edit at 11pm; a red check does.

## Consequences

**Positive**

- Billed minutes per event, simulated across six scenarios against the real workflow files:

  | Scenario | Before | After |
  |---|---|---|
  | docs-only push to `main` | 9 | 6 |
  | docs-only ready PR | 10 | 7 |
  | frontend ready PR | 14 | 10 |
  | infra ready PR | 11 | 8 |
  | **draft PR, any content** | 14 | **2** |

  ≈48% across the set, and ≈86% on the draft-PR path this repo's own workflow invariants make the
  common case.
- The fail-closed detector bug is fixed. A PR can no longer go green on a required check whose
  verification silently did not run.
- Every remaining job either verifies something or reports a required context. None exists purely
  to decide whether another job should run.

**Negative**

- Draft PRs get lint/test/build signal only from the local hook. A `--no-verify` push of broken
  code to a draft branch surfaces nothing until the PR is marked ready.
- There is no nightly backstop in this repo. `job-search-ai-assistant`'s ADR-0053 pairs its
  `CI_HEAVY_CHECKS` gate with a nightly cron precisely because heavy jobs stop running on PRs;
  here nothing was moved off the PR path for *ready* PRs, so the exposure is limited to drafts —
  but if a future change does gate a job behind a repository variable, it must land with a
  scheduled backstop in the same PR.
- The pre-push hook must be installed per clone (`npm run hooks:install`). A clone that skips it
  gets the reduced CI *and* no local gate.

**Neutral**

- Both `frontend.yml` and `backend.yml` emit a check run named `build-and-test`, and the ruleset
  requires a single context by that name. This ambiguity predates this decision and is left alone:
  renaming a required context needs a coordinated ruleset edit, which carries the deadlock risk
  described in the platform-semantics table above. Recorded here as a known latent hazard.
- The `CI_HEAVY_CHECKS` repository-variable lever from ADR-0053 was considered and **not** adopted.
  Nothing here is expensive enough to justify moving it off the PR path for ready PRs; the
  structural savings were sufficient. The lever remains available if Lighthouse or a future e2e
  suite grows costly.

## Alternatives considered

- **`dorny/paths-filter` in place of the hand-rolled detector.** Rejected: it solves the detector's
  correctness problems but not its cost — it still runs as a job, and the job is the billed unit.
  Trigger-level `paths:` and an in-job step both avoid the runner entirely.
- **Keeping one shared `changes` job feeding all four workflows.** Rejected: a workflow cannot
  consume another workflow's job outputs without `workflow_call`, which would have coupled all four
  stacks into one run and made the required-context surface harder to reason about, for a saving
  the two per-workflow fixes already deliver.
- **De-requiring `build-and-test` to allow `paths:` on `frontend.yml`/`backend.yml`.** Rejected:
  editing a required-context set is the operation most likely to deadlock open PRs, and the in-job
  step achieves the same saving with no ruleset change.
- **Merging the `dependency CVEs` matrix into one job.** Rejected: both legs are required contexts
  by exact name. See §4.
- **Self-hosted runners.** Not considered seriously for a personal site — a runner daemon holding
  Azure credentials at rest is a worse trade than the minutes it saves.

## References

- `job-search-ai-assistant` ADR-0053 — the source of the per-job-minimum and pre-push-gate patterns
- ADR-0005 — the Azure bootstrap the `infrastructure.yml` jobs depend on
- Issue #75 — the path-filtering deadlock whose fix the required-check rules here preserve
- `.github/tests/workflow-cost-invariants.py` — the executable form of this decision
