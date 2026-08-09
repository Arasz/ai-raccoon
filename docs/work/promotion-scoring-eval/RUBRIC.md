# Promotion usefulness rubric (0–4)

You are labeling entries from a multi-project agent **memory bank**. Each entry is a chunk of a
document or an organically-written note belonging to ONE project. The question you answer for each:

> **If this entry were promoted into a SHARED tier that every other project's agent sees, how
> useful would it be to those other projects?**

Shared-tier space is scarce. The tier is for durable, portable facts — not for this project's
in-flight coordination, status, or self-description.

## Scale

- **4 — Durable, portable gotcha or measured fact.** Something an agent on a *different* project
  would be materially wrong or slower without. A root cause, a non-obvious API/tool behaviour, a
  measured result with units that settles a decision. Rare.
  Owner examples: "organic measured fact, framework-wide impact"; "UX gotcha: wrong project_id
  returns empty with no error"; "durable gate gotcha: e2e flake root cause CI env".
- **3 — Cross-repo convention, core semantics, or durable methodology.** A rule, contract or
  design decision that generalises past its own project.
  Owner examples: "cross-repo observability convention (BCL-only Meter pattern)"; "core semantics
  useful to all consumer projects"; "security lesson: unsalted digest of guessable slug is
  theater"; "durable review methodology rules".
- **2 — Useful within its project, weakly portable.** Research syntheses, performance
  measurements, explanations that another project could learn from but that are framed around
  this project's specifics.
- **1 — Mostly local.** Plans, reports, changelog entries, docs with some substance but nothing a
  different project would act on.
- **0 — Noise for a shared tier.** Work notes, in-flight coordination (task lists, gates, waves,
  effort estimates), status recaps, directory indexes and tables of contents, transcript/tool-call
  dumps, session handoffs, near-contentless fragments, frontmatter.

## How to judge

- **Judge the text, not the filename.** A file path is a hint about the *kind* of document, but a
  durable fact inside a plan is still a durable fact, and a status dump inside an ADR is still a
  status dump. Read the value.
- **Portability is the axis.** "Is this true and useful outside this repo?" not "is this good
  writing?" and not "is this important to this project?".
- **Chunks are partial.** Many entries are one chunk of a longer document. Judge the chunk on its
  own — a chunk that is only a heading, a link list, or a table of contents is 0 even if the
  document it came from would be a 3.
- **Test counts and command output are not measurements.** "3272 passed, exit 0" is status, not a
  measured fact.
- **Be strict at the top.** In a calibrated set, roughly 5% earn a 4 and 10% a 3. If you are
  labeling more than a fifth of entries ≥3, you are being too generous.

## Output

Write **every** input id exactly once. No omissions, no extras.
