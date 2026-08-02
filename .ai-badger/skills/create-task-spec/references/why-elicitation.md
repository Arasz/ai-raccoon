# Why this skill interrogates instead of drafting

Evidence behind the procedure in `SKILL.md`. Read when the shape is being questioned or changed,
not on every turn.

## The failure this design exists to prevent

A skill that drafts a plausible specification for someone to approve produces a document that
reads as agreed but encodes the model's guesses. Two dispositions combine to cause it:

- Language models fill blanks rather than leave them open.
- People accept a plausible list rather than generate a competing one.

Shown six candidate rules, a person edits those six. They do not produce a seventh. The knowledge
that was supposed to be extracted stays in their head, and the spec's authority is borrowed from
a name that never supplied its content. Because the artifact still *looks* complete, the failure
is silent — which is what makes it worth designing against rather than merely noting.

## Why Gherkin, given the toolchain is out of scope

Nothing here runs a BDD framework. The format was chosen for three properties that hold with no
tool attached:

1. **Its grammar is a question schema.** Required slots that are empty are questions that must be
   asked. The interview script is derived from the artifact rather than chosen by whoever wrote
   the checklist.
2. **Incompleteness is structural.** A `Rule` with no example and an example with no steps are
   facts about the file, not judgements about the conversation. `scripts/spec_holes.py` counts
   them, so "are we done?" has an answer that does not depend on the agent's opinion.
3. **It is unambiguous for the reader that matters.** Given/When/Then constrains a behavioural
   claim more tightly than prose, which is the point when the next reader is an implementation
   agent.

The keyword set is standard. `Ability` and `Business Need` are plain-English synonyms for
`Feature` — verified against `gherkin-languages.json` in the Cucumber parser, which defines 80
spoken-language dialects.

## Why a scanner and not a parser

Gherkin is line-oriented: every non-blank line starts with a keyword. The narrow question asked
here — which rules have no examples, which examples have no steps — needs no AST. That keeps the
skill inside ai-badger's dependency floor (stdlib, plus `jsonschema` and an optional `pyyaml`),
which a Gherkin parsing library would have broken for no gain.

Doc strings are the one place a naive line scan goes wrong: prose inside `"""` may contain the
word `Example` without being one. The scanner skips doc-string bodies for that reason, and a test
pins it.

## Prior art, and which pole to copy

Two close implementations sit at opposite ends of the axis this skill cares about.

**Proposal-first.** SpecBinder ships Claude Code commands over the same stages, instructing the
agent to "suggest 2–6 plausible business rules" and "extrapolate scenarios". That is the
anti-pattern above, stated as a procedure. Its prose is also GPL-3.0 against this project's MIT,
so it must not be copied — the convergence on the *stage sequence* is citable; the text is not.

**Question-first.** `dotnet-claude-kit`'s `/spec` (MIT, © Mukesh Murugan) contracts never to
assume: a "probably" is a question to ask rather than a decision to make, questions come in rounds
of 3–5, a dimension is finished when a follow-up round yields nothing new, and "I don't know"
becomes a deferred decision with a fallback chosen at the time. Those mechanics are borrowed here
with attribution. The difference is where the structure comes from: that skill uses nine chosen
dimensions, this one uses the target format's grammar, which supplies the stopping condition as
well as the questions.

## Why one skill rather than two

An earlier shape had a second skill whose only job was to call this one and then `task`. It was
dropped: this repository already chains skills without a wrapper — `differential-feature-refactor`
invokes `owner-gate-review` and reads its result back — and a third entry point would cost a scope
declaration, a plugin sync, a docs row, and a lasting ambiguity about which skill to invoke.

## Full research record

`docs/work/2026-08-01-gherkin-spec-elicitation-research.html` — the claim-by-claim assessment,
including the two premises that did not survive verification.
