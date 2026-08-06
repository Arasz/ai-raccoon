# Gherkin spec mechanics — pitfalls found in practice

## spec_holes.py (create-task-spec) — @deferred tag placement

The scanner (a line-oriented state machine) applies a tag to **the line it directly
precedes**:

- `@deferred` immediately before a `Scenario:` line → that scenario may be step-less
  (recorded as a deferred hole, not an open one).
- `@deferred` before a `Rule:` line defers ONLY the rule-without-example hole. A scenario
  nested under that rule does NOT inherit the deferral — a step-less scenario there is an
  **OPEN hole** and the script exits 1.

Concrete failure seen: a `@deferred`-tagged Rule whose single scenario had no steps passed
"0 still open" only after the tag was ALSO placed on the Scenario line.

Rules of the gate: every `Rule:` needs ≥1 `Scenario:`/`Example:`; every non-deferred
Scenario needs ≥1 step; `#` comments and ```/""" doc strings are skipped; exit 0 = no open
holes = the spec is structurally complete. Run it on every emitted `.feature` and report
its output verbatim ("an unread check is not a check").

## Owner-gate review form — generation, verification, ingest

**Generation.** `cp` the skill's `references/form-template.html` → `<docs>/work/<date>-<slug>-review.html`,
then patch ONLY the `var CONFIG = {...}` and `var DECISIONS = [...]` blocks. Never re-emit
the ~9KB template by hand. `CONFIG.storageKey` MUST be unique per review (every `file://`
page shares one localStorage origin — a generic key loads another document's answers);
`outName` must match the watch target; `expectedDir` = the watched folder.

**Verification gotcha (cost an hour in practice).** The form's two `<script>` blocks share
browser-global scope — `CONFIG` and `DECISIONS` are page globals declared in block 0 and
read in block 1. A smoke run that executes block 1 via `new Function(...)` in isolation
throws `CONFIG is not defined` — a **harness bug, not a form defect**. Run both blocks in
ONE `vm` context with DOM stubs (see `scripts/verify-gate-form.mjs`). DOM stubs need
`classList.toggle` too (`paint()` calls it).

**Protocol.** Start the watch (capped: 720 × 5s) BEFORE telling the reviewer the form is
ready; pre-create nothing that could read as an answer; tell the reviewer to press Clear
first if they've reviewed any `file://` form before. Ingest by READING the file, not the
notification: check the trailing `<!-- end refinement feedback -->` marker (missing = caught
mid-write, re-read). Read every note in full — a note can be a counter-question, which means
the decision stays open. Reconcile `## Not answered` explicitly: silence is not consent.

**External editors.** The user's editor may reindent files between your `write_file` and
`patch` (patch warns "modified since you last read"). Re-read before patching, and normalize
inserted blocks to the file's existing indentation (this repo's Gherkin convention: Rule 4 /
Scenario 8 / steps 12 spaces; a 6-space block from an earlier write stood out as the only
mis-indented region).

## Sub-agent gate discipline

Packages that "passed alone" still fail together: the P5 sub-agent reported a clean
Chunking filter while P7's mid-flight test project broke the shared compile — the join gate
(full build + full test, run by the orchestrator after ALL packages land) is the only
trustworthy signal for a wave.
