# Amendments and the record contract

A corrected document must show **what was wrong and why**, not just the fixed text. A silent fix
leaves every reader who quoted the old text with no way to discover they were wrong.

## The amendment table — the ledger

Every document that has ever been corrected carries one table, under a final `## Amendments`
heading, newest row last:

```markdown
## Amendments

| Date | Commit | Reason | Change |
|---|---|---|---|
| 2026-01-15 | 1afb2ca6 | The monthly budget cap was quoted from a brief; it does not exist | Removed the cap from §2 and the cost table |
```

- **Date** — ISO, the date of the correction.
- **Commit** — the SHA that landed the correction. Short SHA is fine; it must resolve
  (`git cat-file -e <sha>`).
- **Reason** — what was wrong, in the past tense. Not "clarified wording". If you cannot name a
  false statement, you are not amending, you are editing — no row.
- **Change** — what you did about it.

**There is no `Who` column.** With a small team and N agents the column reads the same value
forever, which is zero information. Version control already answers who, and the SHA makes the row
falsifiable in a way a name never is.

## Prose beneath the table — for substantial corrections only

**The table is the ledger — always required. The prose block is required only when the correction
is substantial**: a reversed conclusion, a retracted number, a premise that turned out false,
anything a reader may have already acted on. A typo fix gets a row and nothing else.

The prose block is a dated `###` heading naming what was wrong, then a `**Reason.**` paragraph:

```markdown
### 2026-01-15 — §3's "unmeasured" premise was a stale read. Prior 1 **is** measured.

**Reason.** This research ran while the dataset build was still writing; it read …
```

When both exist, the row's `Reason` is the one-line version of the prose heading. They must not
disagree; if writing the row makes the prose look wrong, the prose is wrong.

**Never script an amendment's reason.** A generated reason is a sentence that sounds like an
explanation and explains nothing, and it is worse than an empty row because it stops anyone looking
further. The date, the commit and the fact that a row is owed are all mechanical; the reason is the
one part that is judgement, and it is the only part a reader actually needs.

## The record contract

A project that keeps a documentation ledger appends one entry per change and regenerates every
projection from it: the docs changelog, the index, and the frontmatter `version:`/`updated:` fields.
The rules below hold whether that append is a tool invocation or a hand-written line.

- **`version:` is a projection, not a field you edit.** It is the count of ledger entries for that
  path. A hand-bumped `version:` is a version the ledger cannot justify; a check that catches it is
  correct, and the fix is another ledger entry, never an edit to the ledger.
- **The ledger append is the commit point.** Everything after it is an idempotent projection that
  can be rebuilt. If a run dies mid-way, rebuild the projections; do not hand-repair the index or
  the changelog.
- **A content hash is what makes a check able to fail.** Two numbers written by the same process
  always agree; a hash over canonicalized frontmatter (minus `version`/`updated`) plus body is what
  detects a document edited without a record.
- The ledger file is append-only and should be marked `merge=union` in `.gitattributes` so
  concurrent branches merge without conflict. Never rewrite or reorder existing lines.

**A documentation change that was not recorded did not happen.** Reporting an update complete
without its ledger entry is a failed run, not a minor omission: the projections are then stale, and
the next person inherits the failure.

**If the project keeps no ledger**, the obligation collapses to one line you must still satisfy:
the amendment table is in the file, its Reason names a false statement in the past tense, and the
change is committed. Nothing above is optional because it is unautomated.
