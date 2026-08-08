## ADR-vs-code drift: contract points vs factual points

When an ADR and the code disagree, do NOT reflexively "fix the ADR to match the
code". Split the drift by what kind of statement it is:

- **Contract points** — the ADR *promises* behavior the code must emit (tag
  tables, status semantics, instrument names, DI shape, error shapes). Here the
  CODE is wrong; align code to the spec with TDD (write the failing test
  asserting the promised emission, then implement). The ADR is the accepted
  spec; a "docs refresh" task is not a license to downgrade the spec to match
  the code. When the task says "check if all metrics/traces are emitted", that
  IS a spec-vs-emission audit — trace-side promises the code never kept (a
  promised `result` tag, promised `SetStatus(Ok)` on success) are the exact
  findings to fix in code, not delete from the doc.
- **Factual points** — the ADR *describes* the implementation (tool counts,
  "instrumentation is inlined 3–5 lines" vs an extracted helper class, which
  Future-evolution item already landed). Here the DOC is wrong; refresh it in
  place, smallest edit that makes the sentence true.

Before editing, check the ADR regime: read the ADR README's stated policy
("immutable, frozen — never edited") AND `git log --follow -- <adr-file>` for
observed practice. They often disagree: the project's ADR README declares
immutability while ADR-0002/0003/0004 each carry post-acceptance correction
commits (e.g. "fix: review corrections — …"). Working rule: immutability
protects the DECISION (the architectural choice, instrument names, DI
registration); factual snapshots inside the ADR get corrected in place like any
other doc. If the owner enforces strict immutability, the fallback is a NEW
numbered ADR amending the old one — offer it in the report, don't preempt it.
Also re-check the ADR's own "Future evolution" list: an evolution item that has
already shipped (e.g. "helper extraction after N tools are instrumented") turns
the old "current" description into fact-drift that the refresh must record.

Worked example (the project, 2026-08-06): ADR-0002 drifted in 4 places — tool
count 17→19 (fact), "inlined instrumentation" vs the shipped
`ToolExecutionActivity` helper (fact; the ADR's Future-evolution #4 had
landed), a promised `result` activity tag the code never set (contract), and
promised `SetStatus(Ok)` on success that code only did on the error path
(contract). Ruling: TDD-added `SetStatus(Ok)` + `result` tags (success AND
error paths) to the code; edited the ADR's factual sections in place; decision
substance untouched.
