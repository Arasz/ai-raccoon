# <Feature> — Differential

**Status:** Awaiting feedback | Annotated | Superseded by `<spec path>`
**Date:** YYYY-MM-DD
**Verified against:** `main` @ `<sha>` (+ note any uncommitted working-tree state)
**Authority set:** ADR-NNNN, ADR-NNNN, FR-x.y, NFR-n, `<ruling>`
**Not authoritative about the running system.** This is a dated decision artefact, not a system
description. It goes stale the moment work lands. The canonical description of this feature lives
in `<canonical doc>`; if the two disagree, the canonical doc wins and this file is stale.

Every numbered section below is required, and section 3 requires all four views with both
diagrams each. A missing section is an incomplete document, not a shortened one. Do not carry the
`<!-- REQUIRED -->` markers or this instruction line into the finished document.

---

## 1. Current state <!-- REQUIRED -->

What is in the tree today. Every claim carries `path:line`. Anything you could not verify against
the tree is marked `[UNVERIFIED]` inline, with what would verify it.

> A citation to another document is **not** a citation to the tree. Docs are candidate intent;
> only the tree is current state.

| # | Claim | Evidence |
|---|-------|----------|
| C1 | `IFeedMonitor` has two implementations and one live caller | `src/.../CheckFeedActivity.cs:19` |
| C2 | `[UNVERIFIED]` The timer recurs beyond the first tick | would need a deployed run over ≥3 intervals |

**What is solid and must not be touched by this refactor:** …

<!-- Undefined points about the current state go HERE, in this section. -->

#### UP-1 — <one-line question>

**Question:** One precise, answerable question. No compound questions.

| # | Proposition | Trade-off |
|---|-------------|-----------|
| A | … | … |
| B | … | … |
| C | … | … |

**Feedback:**
<answer question="1">
UNANSWERED
</answer>

---

## 2. Target state <!-- REQUIRED -->

What it becomes, and **which decision authorises it**. Every row needs an authority. A plan or
review document is not an authority — if that is all you have, the row is `[UNVERIFIED]` and gets
a UP block instead.

| # | Target | Authorised by |
|---|--------|---------------|
| T1 | Single ingest path | ADR-0024 D2 |
| T2 | `[UNVERIFIED]` Archive-upload path | no ADR — see UP-2 |

**Needs a new ADR:** T2, T5 (architecture-level; CLAUDE.md requires one before implementation).

<!-- Undefined points about the target go HERE. -->

---

## 3. Flow views <!-- REQUIRED: all four, each have-vs-will-have -->

Each view is one Mermaid diagram for **Have** and one for **Will have**, plus a short delta list.
A view without both diagrams is incomplete.

**When a UP leaves the target undecided:** still draw **Will have**, at the level the authority set
does fix, and label the undecided nodes `TBD per UP-<n>`. Never omit the diagram, and never invent
the answer to make it drawable.

### 3.1 Architecture flow — components and their boundaries

**Have**

```mermaid
graph TD
```

**Will have**

```mermaid
graph TD
```

**Delta:** …

### 3.2 Data flow — what moves, in what shape, where it is persisted

**Have**

```mermaid
graph LR
```

**Will have**

```mermaid
graph LR
```

**Delta:** … (name the container and partition key for every persisted shape)

### 3.3 Logic flow — decisions, branches, ordering

**Have**

```mermaid
flowchart TD
```

**Will have**

```mermaid
flowchart TD
```

**Delta:** …

### 3.4 Integration flow — external systems, entry points, triggers, contracts

**Have**

```mermaid
sequenceDiagram
```

**Will have**

```mermaid
sequenceDiagram
```

**Delta:** …

<!-- Undefined points about a specific flow go inside that flow's subsection. -->

---

## 4. What this does not cover <!-- REQUIRED -->

The honest boundary. Name what was deliberately excluded, what was out of reach, and what a
reader must **not** conclude from this document.

- Not covered: …
- Could not verify: … (and why)
- Do not conclude from this document that: …

---

## Undefined-point block — the shape, for reference

Never collect these at the end. Each one lives in the section it belongs to. There is no
"Open Questions" section in this template.

```markdown
#### UP-<n> — <one-line question>

**Question:** One precise, answerable question.

| # | Proposition | Trade-off |
|---|-------------|-----------|
| A | … | … |
| B | … | … |
| C | … | … |

**Feedback:**
<answer question="<n>">
UNANSWERED
</answer>
```

- Numbered sequentially across the whole document; heading matches `^#### UP-\d+ — `.
- **Exactly three** propositions, lettered A/B/C, each with a stated trade-off.
- The human replaces the `UNANSWERED` token inside the block with their decision (a bare "B" is a
  valid answer). That token appears nowhere else in the document.