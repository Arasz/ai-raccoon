# kind-snapshot

Applies to a Verify/approval-style test, or any `toMatchSnapshot` use. The runner only ever proves
"this changed"; it cannot say which change was the defect, so this kind's rules exist to keep the
technique inside the artifacts it can legitimately speak for (per ruling C7 — a snapshot satisfies
`T1-ORC-01` and fails `T1-SCO-01` alone) and out of the ones a reviewer would have to rubber-stamp.

**`T2-SNAP-01` — A snapshot is a change detector by construction; use it only where the whole shape is the contract.**
- *design:* reach for a snapshot when the artifact itself is the deliverable — a serialized API response, a generated file, a rendered document — never as a stand-in for a business-logic assertion.
- *review:* the snapshot cannot say which part of a diff mattered; standing in for business-logic assertions is the misuse.
- *check:* does the snapshot stand in for a business-logic assertion — no separate, explicit assertion on the specific facts a reviewer cares about anywhere in the test? Evidence: read the test — a snapshot with zero companion assertions on business-meaningful fields is the violation.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-SCO-01`
- *cites:* Google TotT *Change-Detector Tests*; ruling C7.
- *meta:* pass=3 order=14

**`T2-SNAP-02` — Pair every snapshot with explicit assertions on the business facts that matter.**
- *design:* write the specific assertion first, add the snapshot alongside it — never instead of it.
- *review:* the snapshot then catches unanticipated structural change while the explicit assertions carry the meaning, and a reviewer can tell which failure is which.
- *check:* does at least one explicit, non-snapshot assertion exist alongside the snapshot, naming a specific business fact? Evidence: read the test body — a snapshot with no companion `Should`/`Assert` on a named field is the violation.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-SCO-01`
- *meta:* pass=3 order=14

**`T2-SNAP-03` — Scrub every non-deterministic field before comparing: ids, timestamps, ordering, machine names, paths.**
- *design:* normalise or mask every generated field before the snapshot is written, not after the first spurious failure.
- *review:* spurious failures train the team to distrust snapshots, and a distrusted snapshot is worse than none.
- *check:* auto — grep verified/snapshot files for GUIDs and ISO timestamps.
- **severity:** blocker · **evidence:** strong · **flag:** auto · **parent:** `T1-ISO-03`
- *meta:* pass=2 order=11

**`T2-SNAP-04` — Never bulk-accept. A snapshot diff is reviewed like a code diff, and the acceptance is part of the PR.**
- *design:* review each changed snapshot individually before accepting; forbid a CI mode that writes new verified files.
- *review:* "update all snapshots" after dozens of failures approves that many unreviewed behaviour changes at once — the technique's single most damaging failure mode.
- *check:* auto-unless-listed — does the PR show the diff, and is bulk-accept blocked in CI?
- **severity:** blocker · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-PRF-02`
- *meta:* pass=8 order=31

**`T2-SNAP-05` — Keep snapshots small enough to read; a growing snapshot count is a quality metric in its own right.**
- *design:* prefer several small, targeted snapshots over one sprawling one.
- *review:* a snapshot too large to read at review time has already stopped doing its job.
- *check:* is a `.verified` file too long for a reviewer to read the whole diff at PR time? Evidence: the line count of the changed `.verified` file — a large, sprawling snapshot with no smaller targeted alternative is the violation; there is no fixed threshold, only whether a reviewer can actually read it.
- *rationale:* weak evidence — practice consensus, no first-party or measured citation; caps at `major`.
- **severity:** minor · **evidence:** weak · **flag:** argued · **parent:** `T1-STR-01`
- *meta:* pass=7 order=28

**`T2-SNAP-06` — Snapshot only stable, non-visual, serialised artifacts; never a component tree.**
- *design:* legitimate: a generated file, a wire body, an error map. Never legitimate: a rendered UI tree.
- *review:* a component-tree snapshot cannot see layout or computed style either, so it doubles as a `T1-CST-05` blind-runner finding.
- *check:* auto — snapshot calls over a render/mount result.
- *rationale:* not a conflict with `T2-SNAP-01` once split by artifact (ruling C14) — stable/non-visual/serialised yes, a component tree never.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-CST-05`
- *meta:* pass=0 order=1
