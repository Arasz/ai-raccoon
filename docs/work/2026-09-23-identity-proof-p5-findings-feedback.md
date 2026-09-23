# Refinement feedback — ADR-0106 P5 findings — one release blocker, seven residuals

<!-- refinement-form: refinement:2026-09-23-identity-proof-p5-findings:v1 · saved 2026-09-23T11:32:52.176Z · answered 10/10 -->

Source document: `docs/work/implementation/p5-e2e-report.md`

## F1 — Serve tightens an owner-owned, group/world-<em>readable</em> state dir to 0700 instead of refusing it

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F7 — The exit-22 no-bank remedy names <code>--install-scope project</code> when the root is project-scoped

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F2 — <code>serve --restart</code> proves the listener before its identify <code>GET /observability</code>

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F3 — A fallback child that fails its own proof is stopped by PID immediately

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F4 — A stateful-revision (2025-11-25) MCP client makes the proxy start two fallback children — file an issue

**Verdict:** APPROVE

**Notes:**

> MCP client? Explain

---

## F5 — The MCP SDK stdio client's shutdown kills the proxy before prove-then-stop runs — record as ADR-0106 residual + issue

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F6 — Proxy EventIds 690/691 warnings go to stderr even under <code>--quiet</code> — file an issue

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## F8 — <code>model download</code> at a typo root writing <code>models/</code> is accepted as out of D4 scope

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## H1 — The README What's-new line stays untagged; the release-bump step adds the version

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## H2 — The <code>logging-event-ids.md</code> reproduction grep gets fixed in a separate quick-task

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

_(none — every item has a verdict)_

## Reconciliation (agent, 2026-09-23)

- F1, F7, F2, F3 fixed in PR #657; F5 recorded as ADR-0106 residual 13 and #660; F4 → #658;
  F6 → #659; H2 → #661; F8 accepted; H1 left for the release-bump step.
- F4's note ("MCP client? Explain") answered in session: the MCP client is the agent host on the
  proxy's stdio; a `2025-11-25`-revision client reopens its session, which on a squatted port costs
  a second private fallback backend. No secret exposure.
- R1 (added after this form was saved: retry attributes on the security E2E gates) was not answered
  within the prompt window; the recommended `Retry=Never` carve-out was applied (06120aa6) pending
  owner confirmation.

<!-- end refinement feedback -->
