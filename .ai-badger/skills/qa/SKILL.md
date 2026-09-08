---
name: qa
description: >-
  Use when the user wants a question-and-answer session against a context — "qa {context}",
  "I have questions about X", or a question asked against supplied material — or wants the
  session's questions and grounded answers saved as a summary. If no context is given, ask
  for it first; every answer carries its grounding, and the session closes with a summary
  in docs.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [questions, answers, grounding, sessions]
    related_skills: [evidence-first-research, explore-codebase, semantica-knowledge-graph, task]
---

# QA session

A grounded question-and-answer exchange against user-supplied context, closed by a session
summary saved to the consumer repo. Answers are analyzed against the context — never from
memory alone — and every answer names the sources that ground it.

Not the `qa` test-quality persona, which lives in a different namespace (delegation
personas) and means something else. This skill runs Q&A sessions; that persona audits
tests.

## Invocation

`qa {context}` opens a session. `{context}` can be anything — accept it as given:

- inline text pasted into the request,
- a file, directory, or topic name,
- a question asked against material already in the conversation.

No context provided: ASK the user for it first. Do not start answering until the context
exists — a session with nothing to stand on produces ungrounded answers.

## Procedure

1. **Accept the context.** Restate in one line what the session stands on (the pasted
   material, the resolved path, the topic). If a path does not resolve, say so and ask
   again — do not substitute a nearby file silently.
2. **Run the Q&A loop.** The user asks; you answer each question against the context:
   - Analyze the question in the context before reaching for outside sources.
   - Ground every answer per the grounding rule below, in `{answer}` + `{grounding}`
     form. One question, one grounded answer, then the next question.
   - A follow-up that changes the context (new material, corrected premise) restarts
     step 1 for that material; say so in one line.
3. **Close the session.** When the user signals they are done (or the questions run
   out), build the session summary and save it to
   `docs/qa-sessions/<topic-slug>.md` in the consumer repo, creating the directory on
   demand. Then report the path in one line.

Slug rule: lowercase, non-alphanumeric runs become single hyphens, trimmed of leading
and trailing hyphens, truncated to ~64 characters. Re-running a session for an existing
slug overwrites that file.

## Grounding rule

Every answer draws on at least two grounding sources from all available sources, in
this priority order:

1. **Memory / code** — project memory search, the knowledge graph, codebase search.
   This leg is mandatory: an answer with no memory-or-code source is not grounded.
2. **Web** — external documentation, upstream references, published sources.

If no web source exists for an answer, the answer carries the literal note
`couldn't ground in external source` in place of the web leg — never silently drop
the leg, never invent a source to fill it. A source you cannot cite (no path, no
link, no retrieval evidence) is not a source.

## Per-answer format

```
{answer}

{grounding}
```

`{answer}` is the analyzed response, scoped to what the sources support. `{grounding}`
is a short source list naming each source and which leg it satisfies (memory/code or
web), or the `couldn't ground in external source` note. Mark inference as inference —
anything beyond what the cited sources say is labeled, not smuggled in.

## Session summary

Sections, in this order:

1. **Main motive** — what the session was really about, in two or three sentences.
2. **Refined questions** — each asked question restated precisely, disambiguated from
   how it was first phrased.
3. **Structured answers** — the answer to each refined question, with its grounding
   condensed to source names.
4. **Open / unanswered questions** — what was asked but not resolved, and what would
   resolve it. Empty is a valid section; omitting it is not.
5. **Source links** — every source cited in the session, as paths or links.

## Gotchas

- The context is the anchor. An answer that drifts off-context is wrong even when it
  is factually true — say "beyond the session context" before giving it.
- `docs/qa-sessions/` lives in the consumer repo only. Never create it in the
  framework repo, and never treat a missing directory as a missing session.
- Do not bank the summary in project memory instead of writing the file — the file is
  the deliverable. Memory promotion follows the project's own rules, if any.
- Two sources that quote each other are one source. Count independent legs, not
  citations.

## Red flags — STOP

- Do not answer before the context exists.
- Do not present an ungrounded answer as grounded — no sources, no answer; ask a
  clarifying question instead.
- Do not fabricate web sources. The fallback note exists for exactly this case; use
  it.
- Do not turn the session into adjacent work: no refactoring the context files, no
  opening tasks, no "while I'm here" fixes. Offer them after the summary lands.

## Verification Checklist

- [ ] Context existed before the first answer (given, or asked for and received)
- [ ] Every answer has at least two grounding sources, memory/code leg first
- [ ] Missing web leg carries the literal fallback note, never silence or invention
- [ ] Summary has all five sections; open questions listed, not hidden
- [ ] Summary file written to `docs/qa-sessions/<topic-slug>.md`; path reported

## Files

- `SKILL.md` — this file
