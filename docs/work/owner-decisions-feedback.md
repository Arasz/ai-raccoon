# Refinement feedback — AiRaccoon project-scope review — 11 rulings before implementation

<!-- refinement-form: refinement:ai-raccoon-psr-2026-09-22:v1 · saved 2026-09-22T14:36:36.392Z · answered 14/14 -->

Source document: `/tmp/air-review/PHASE5-PLAN-v2.md`

## K1 — F70 — how should the proxy/CLI stop handing the token to an unidentified listener?

**Verdict:** APPROVE

**Notes:**

> lets do a

---

## K2 — F22 — stamp the surviving agent-requested row, or exempt it?

**Verdict:** APPROVE

**Notes:**

> stamp

---

## K3 — F31 — does re-created content win over its own tombstone?

**Verdict:** APPROVE

**Notes:**

> wins

---

## K4 — F37 — is exit 130 the wanted interrupt code?

**Verdict:** APPROVE

**Notes:**

> 130

---

## K5 — F24 — refuse `context`+`workspace_id`, or let `workspace_id` win?

**Verdict:** APPROVE

**Notes:**

> workspace win - sandbox has priority

---

## K6 — F52 — absolute-relevance floor, or an explicit unranked marker?

**Verdict:** APPROVE

**Notes:**

> both

---

## K7 — F7 — which exit code for a bank that exists but is not SQLite?

**Verdict:** APPROVE

**Notes:**

> BankCorrupteded error code, first available

---

## N1 — F38 — CLI-scoped idle timeout, or backend ownership?

**Verdict:** APPROVE

**Notes:**

> leave the backend running

---

## N2 — F49/F39 — in scope for Wave 3, or explicitly out?

**Verdict:** APPROVE

**Notes:**

> both

---

## N3 — F9/F10 — re-tighten the ratchets and inject the alias map, or accept the slack?

**Verdict:** APPROVE

**Notes:**

> tighten

---

## N4 — F63 — must a tagged release be installable, and under which package id?

**Verdict:** APPROVE

**Notes:**

> no - there is no relation like that

---

## N5 — F8/F64 — revive the flake ledger and emit a retry diagnostic, or remove both halves?

**Verdict:** APPROVE

**Notes:**

> remove both

---

## N6 — F42 — who owns `quiet.log`?

**Verdict:** APPROVE

**Notes:**

> server owns it

---

## N7 — F26 — is the chunk the intended delete unit?

**Verdict:** APPROVE

**Notes:**

> memory_delete should remove whole memory - N rows

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->