---
name: create-task-spec
description: >-
  Use when a feature idea needs to become an exact, agreed specification before anyone builds it —
  "spec this out", "create a task spec", "turn this idea into requirements", "what exactly should
  we build". Interrogates the person for what they know instead of proposing content for them to
  approve, using Gherkin's own grammar to decide which questions must be asked and when the
  document is complete. Emits a .feature behavioural contract plus a spec.json manifest that the
  task skill consumes.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [specification, gherkin, requirements, contracts]
    related_skills: [task, behavioral-contracts]
---

# create-task-spec

Turns a rough idea into a specification by **asking**, not by drafting. The person supplies the
knowledge; this skill supplies the structure and the refusal to guess.

The structure is Gherkin. That is not a formatting choice — the grammar decides which questions
get asked and when the interview is over. Every required slot that is empty is a question. Every
rule with no example is a hole a script can point at. The loop stops when the document is
structurally complete, not when the conversation has gone on long enough.

## The contract

**Never assume.** Every gap in the idea becomes a question. If you catch yourself thinking
"probably", "presumably", or "the usual way" — that thought is a question to ask, not a decision
to make.

**Propose options, never content.** A person shown six plausible rules stops generating a seventh
and starts editing someone else's list, and the spec ends up encoding your guesses under their
name. When they genuinely cannot answer without seeing the shape of a choice, offer two or three
alternatives with trade-offs and a recommendation — and let them pick. A recommendation is never
silently applied.

**"I don't know" is a legal answer.** It moves the point to the decision gate with an explicit
fallback the person chooses now. Silent deferral is forbidden — an unanswered question that
quietly disappears is the failure this skill exists to prevent.

**Answers are requirements.** Record each one into the draft as it arrives, rather than holding
the conversation in your head and writing at the end.

## Stage 00 — Orientation (only when invoked with no idea)

If the invocation carried no idea (`/create-task-spec` with nothing after it), do not open with a
question. Explain the process first, in about five lines: that you will ask in rounds rather than
draft something for review, roughly what the stages are, that "I don't know" is a valid answer
that becomes a recorded deferral, that it ends at a decision gate they rule on, and that it
produces two files. Then ask whether they are ready and what the idea is.

If the invocation *did* carry an idea, skip this stage entirely and start at 01 — an explainer in
front of someone who has already told you what they want is friction, not orientation.

## Stage 01 — Restate and confirm

Say back what you understood, in your own words, in one paragraph. End with: "Is this the idea?
What did I get wrong?" Do not start questioning until they confirm — questioning the wrong idea
wastes the whole session.

## Stage 02 — The ability header

Three slots, three questions, no invented answers:

- **Who specifically?** A role, not "the user". A generic persona produces generic rules.
- **What do they want to do?** The capability, not the button. This is where UI-coupled phrasing
  first creeps in — challenge it here rather than at step level.
- **What changes for them?** If the benefit cannot be stated, the feature is not justified yet;
  say so rather than writing a placeholder.

## Stage 03 — Rules, until dry

Ask what must always be true. Then ask what else. Keep going until a round produces nothing new —
that is the stopping test, not a target count. A rule is worth recording when it could be
violated; "the system should work well" cannot be, and is a prompt for another question.

## Stage 04 — Scenarios per rule

For each rule, three prompts: a case where it plainly holds, one that nearly violates it, and one
that fails. Titles only at this stage. Step-less scenarios are legitimate here — they are the
visible queue of what is still to elicit, and `spec_holes.py` counts them.

## Stage 05 — Steps

Fill the queue, challenging phrasing as you go:

- A `Then` that inspects internal state is not an outcome. Ask how they would know it worked
  without opening the database.
- A second `When` in one scenario means it is really two scenarios. Split it.
- No two steps in a scenario may share identical text, and a file holds exactly one `Feature`.

A step may reveal a case nobody has considered. When it does, go back to 04 — that is the process
working, not a failure to converge.

## Stage 06 — The decision gate

Every "not sure", every contradiction between answers, and every remaining hole becomes one
decision card. Run `owner-gate-review` if it is present, feeding it those points; otherwise ask
for a ruling on each one in turn and record the verdicts yourself. **Nothing is emitted while a
card is unanswered.**

Reconcile explicitly: a card nobody answered is not agreement. Re-ask it or record it as deferred
with the fallback they chose.

## Stage 07 — Emit

Write two files, which hold different things and cannot be derived from one another:

- `<Name>.feature` — the behavioural contract. User story header, `Rule` blocks, scenarios with
  steps, and any deferred item left step-less and tagged `@deferred`. Use the `.feature`
  extension; it is the conventional one.
- `spec.json` — the manifest `task` consumes: scope and explicit out-of-scope, non-functional
  requirements, target paths and constraints, gate verdicts with their provenance, deferred
  decisions with their chosen fallbacks, and the spec file's path.

Then verify what you wrote, rather than asserting it is complete:

```
python3 .ai-badger/skills/create-task-spec/scripts/spec_holes.py <spec>.feature
```

It exits non-zero while any hole is still open, and zero once every remaining gap carries a
deferral. Report its output — an unread check is not a check.

Optionally render the spec as a page for review or sharing:

```
python3 .ai-badger/skills/create-task-spec/scripts/render_spec.py <spec>.feature --out docs/work/<date>-<slug>-spec.html
```

## Handing off

`task` consumes the manifest: give it the `spec.json` path instead of freeform text, and its
planning phase gets an agreed contract plus explicit constraints. Acceptance becomes exact —
every non-deferred scenario satisfied.

## Gotchas

No environment-specific gotchas known.

## What this skill is not

It is not brainstorming. Brainstorming explores *what to build* and converges on an idea; this
starts once that is settled and converges on *exact meaning*. If the idea itself is still open,
that belongs upstream — `superpowers:brainstorming` covers it where that plugin is installed;
otherwise settle the idea in conversation first, then come back.

> Why this shape, and the research behind it: read `references/why-elicitation.md` **when the
> contract shape is questioned**.
