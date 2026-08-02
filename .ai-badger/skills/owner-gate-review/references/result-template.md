# Result shape

Exactly what `form-template.html` emits. Parse against this; do not guess.

## Contract

| Element | Rule |
|---|---|
| `# Refinement feedback — <title>` | first line, always |
| `<!-- refinement-form: <storageKey> · saved <ISO> · answered <n>/<total> -->` | provenance; `answered` is the count with a verdict |
| `## <id> — <claim>` | one per **answered** item, in form order. `<id>` is the join key back to the source document |
| `**Verdict:** APPROVE\|CHANGE\|REJECT\|DEFER` | uppercase, exactly one of four |
| `**Notes:**` then a blank line then blockquoted text | **multi-line, unbounded** — each note line is prefixed with `> `, so a note containing `---` cannot become the item separator. Strip the `> ` prefix when ingesting. |
| `---` | ends each answered item |
| `## Not answered` | always present, even when empty |
| `<!-- end refinement feedback -->` | last line. **Absent ⇒ the file was read mid-write. Re-read it.** |

## Reading it

- The note runs to the `---`, not to the end of the line. A single-line read is the exact failure
  this format was built to remove.
- Notes are blockquoted in the emitted form. Strip one leading `> ` from each note line while
  preserving blank lines and all other content.
- A note may be a counter-question. That is an answer, and it means the decision stays open.
- Items under `## Not answered` are **open**, never agreed. An item can appear there *with* a note
  and no verdict — read that note, it is usually the reason the reviewer could not decide.

## Example

```markdown
# Refinement feedback — Ten decisions, two corrections, five open items

<!-- refinement-form: refinement:2026-01-15-import-pipeline:v1 · saved 2026-01-15T09:14:02.113Z · answered 2/4 -->

Source document: `docs/work/2026-01-15-import-pipeline-design.md`

## C1 — ADR-0031's body rule is wrong and gets amended

**Verdict:** CHANGE

**Notes:**

> 200 chars is too tight — the boilerplate plain part on the rejection template is 240.
> Make it 400 and put the threshold in config, not a const.

> Second thing: record *both* parts' lengths in provenance, not just which one won.

---

## D1 — The template header is the discriminator

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

- D2 — Admit by class, refine by template (note only, no verdict)

  Can't rule on this until the enumeration lands. What's the upper bound on the template set?

- O4 — Is ArchiveImporter a real extension point?

<!-- end refinement feedback -->
```