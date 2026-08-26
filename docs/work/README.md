# work/

Dated work records: plans, designs, research, reviews, incidents, backlog. Every file is
named `YYYY-MM-DD-slug.md` and is a snapshot — it records what was true when it was
written, not what is true now.

## Contents

Active records:

| File | What it is |
|---|---|
| [2026-08-26-doctor-memory-embedding-research.md](2026-08-26-doctor-memory-embedding-research.md) | Research record (task `doctor-feature-match`): doctor's code-engine report as the pattern to mirror, the memory-side facts it omits, and two live findings — a bank reporting HEALTHY with an open migration and 47,723 pending rows, and the migration relay draining with no logs or metrics at all. §3.3 carries a struck-and-corrected claim; §6 the live probe; §8 the witnessed gate run. |
| [2026-08-26-doctor-parity-moe-p1-contract.md](2026-08-26-doctor-parity-moe-p1-contract.md) | MoE planning lane P1 (output contract): the frozen doctor line set, exit-code ruling (24), and the shared-component shape. Corrects the record's §3.3. |
| [2026-08-26-doctor-parity-moe-p2-implementation.md](2026-08-26-doctor-parity-moe-p2-implementation.md) | MoE planning lane P2 (implementation shape): the extraction, queries, degraded states, formatting, work-package split. Superseded where the integrated brief differs. |
| [2026-08-26-doctor-parity-moe-p3-tests.md](2026-08-26-doctor-parity-moe-p3-tests.md) | MoE planning lane P3 (test design): characterisation, anti-swap gates, migration states, collision audit. Superseded where R2 differs. |
| [2026-08-26-doctor-parity-moe-p4-observability.md](2026-08-26-doctor-parity-moe-p4-observability.md) | MoE planning lane P4 (runtime observability): the migration relay emits no logs/metrics; extracts `EmbedDrainReporter`, event ids 1008-1013. |
| [2026-08-26-doctor-parity-moe-r1-arch-review.md](2026-08-26-doctor-parity-moe-r1-arch-review.md) | Independent architecture/correctness review: leaner extraction, event-id allocation, exit-24 ruling, the `model reset` permanent-lockout defect, ADR rulings. |
| [2026-08-26-doctor-parity-moe-r2-qa-review.md](2026-08-26-doctor-parity-moe-r2-qa-review.md) | Independent QA/test-honesty review: ran every gate command, walked the anti-swap mutations, found the guard-swap and intra-descriptor blind spots. |
| [2026-08-26-doctor-parity-moe-r3-ops-review.md](2026-08-26-doctor-parity-moe-r3-ops-review.md) | Independent ops/docs/release review: doc-surface set, stale-sample drift gate, minor-bump precedent, exit-code compatibility break, stuck-bank runbook, manual-verification rows. |
| [2026-08-26-doctor-parity-integrated-brief.md](2026-08-26-doctor-parity-integrated-brief.md) | Integrated implementation brief: frozen contract, component shape, binding test rows, release path (one PR, one minor bump, exit 24 called out). |
| [evidence-2026-08-26-stuck-bank/](evidence-2026-08-26-stuck-bank/) | Read-only capture of the owner's stuck bank (db + WAL + hashes) taken before the live drain, for the manual exit-24 row and P4 stale-lease fixtures. |
| [2026-08-25-fast-lane-flakes-diagnosis.md](2026-08-25-fast-lane-flakes-diagnosis.md) | Diagnosis (task diagnose-fast-tests): the main-run fast lane failed on two documented flake classes — the pack test's MSBuild host crash (exit 134, untriagable because build-fast lacks the crash-dump env) and a port release→rebind race (both racers got PortInUse). Lane ran 14m31s+ vs 168s on the identical PR run: crashed-pack MSBuild zombies + co-tenant load at a 15-min budget. Fixes proposed, not yet implemented. |
| [2026-08-24-vec-code-unfix-dim-plan.md](2026-08-24-vec-code-unfix-dim-plan.md) | Plan (rev 1, review round 1 folded) + implementation record: `vec_code` dimension-agnostic via the generalized `VecDimensionReconciler` (WP1-WP5, task `task/vec-code-unfix-dim`). Owner-gated 2026-08-24; implementation on the same branch. |
| [2026-08-07-skill-discovery-mcp-server.md](2026-08-07-skill-discovery-mcp-server.md) | Active research, not yet implemented |
| [2026-08-22-s6a-fixture-replacement-design.md](2026-08-22-s6a-fixture-replacement-design.md) | Design + execution record for replacing the private `jsaa-memory.db` retrieval fixture with a corpus built from this repo's own public docs (ai-raccoon#414 S6a, ADR-0090). §9 amends the plan with what execution measured differently. |

Frozen, build-embedded Gherkin bundles — referenced by
`tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj`'s `ReqnrollFeatureFiles`; never move,
rename, or reformat these paths:

| Bundle | What it is |
|---|---|
| [features-agent-memory/](features-agent-memory/) | `agent-memory.feature` (29 scenarios) + `spec-issue-1.md` design rationale, linked from `docs/reference/agent-memory-server.md` |
| [features-native-memory/](features-native-memory/) | `native-memory.feature` + `spec.json` native-store scope |

`archive/` holds superseded sprint records — implemented plans, research whose
conclusions now live in an ADR or reference doc, and reports whose verdicts already
shipped. Kept for provenance (some are cited directly as ADR evidence trails), not for
day-to-day reading; consult the relevant ADR or `docs/reference/`/`docs/explanation/`
doc first.
