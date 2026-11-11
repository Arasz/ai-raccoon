---
name: simplified-technical-english
description: >-
  Use when writing anything the user reads — a response, an answer, a final report, a status
  update, a question, a decision request, a warning, step-by-step instructions, or a subagent
  hand-back. Applies the ASD-STE100 Simplified Technical English writing rules to user-agent
  communication, including readers whose first language is not English.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [communication, ste, asd-ste100, simplified-english, reports, responses, plain-language]
    related_skills: [humanizer, status-report, documentation]
---

# Simplified Technical English for agent messages

ASD-STE100 is a controlled language for technical text: few words, one meaning for each word,
short sentences, and fixed shapes for instructions, descriptions and warnings. A reader
understands an STE message on the first read, also when English is their second language.

This skill applies the STE writing rules to every message an agent writes to the user. It does
not apply the STE dictionary word for word. It keeps the rules that make a message clear.

## Scope

- **Rules apply to:** the prose of a message — sentences, list items, table cells, headings.
- **Text that stays exactly as it is:** code blocks, inline code, commands, file paths,
  identifiers, versions, URLs, numbers, and quoted error text or quoted user words. Each of
  these counts as one word.
- **Repository documents** (READMEs, docs, ADRs) follow their own instructions. Use this skill
  for them only when those instructions ask for it.

## Message shape

Write each message in this order. Leave out a part that has no content.

1. **Result** — one or two sentences: what is done, what is not done, and the state now.
2. **Warnings** — a hazard the reader must know before they act (see rule 7).
3. **Evidence** — what you ran or read, and what it showed.
4. **Decisions** — each decision the user must make, as a direct question with your
   recommendation.
5. **Next step** — the action that comes next, and who does it.

When the message contains a procedure, write it in this shape. A caution is a line inside the
list, directly above the step that has the risk:

```
**Warning:** <command>. <risk>.          ← a risk for the whole task, before step 1
1. <step>
2. <step>
   **Caution:** <command>. <risk>.       ← the risk of step 3, above step 3
3. <step that has the risk>
```

To answer "what happens if…", write a description after the procedure. Do not label that
description as a warning or a caution.

## Rules

Numbers in the first column match the sections of ASD-STE100 Part 1.

| # | STE section | Rule for agent messages |
|---|---|---|
| 1 | Words | Use one word for one meaning, and use that word every time. Use the user's names for project things. Use common words — "use", not "leverage"; "start", not "kick off". Use no slang, idiom or metaphor ("is up", "flaky", "in flight", "landed"). Use technical names (tools, APIs, files) as they are. |
| 2 | Noun clusters | Use a maximum of three nouns in a row. Break longer clusters with "of", "for" or "in": "the timeout of the queue test", not "queue test timeout fix". |
| 3 | Verbs | Use the simple present, simple past, simple future, the imperative and the infinitive. Use the active voice. Do not use "-ing" forms as verbs ("is syncing" → "syncs"). Do not use "would", "could" or "might" to state a fact; state the fact or state the condition. Use a verb for an action, not a noun ("check the log", not "do a check of the log"). |
| 4 | Sentences | Write one topic in each sentence. Do not remove articles, "that", or verbs to make a sentence shorter. Do not use contractions ("don't" → "do not"). Use a vertical list for three or more items. |
| 5 | Procedures | Write each step as a command (imperative). Write one instruction in each step, except two actions that occur at the same time. Put a condition first: "If the build fails, do X". Use a maximum of 20 words in each sentence. Do not refer forward ("see below") inside a step. |
| 6 | Descriptions | Put the most important information first. Use a maximum of 25 words in each sentence. Write one topic in each paragraph and a maximum of six sentences in each paragraph. |
| 7 | Safety instructions | Put each warning or caution **immediately before** the step it applies to, inside the procedure. A warning for the whole task goes before step 1. Never put a warning after the procedure or point to it ("see the caution below"). Start the warning with a clear command, then give the risk: "**Warning:** Do not delete the old key before the new key works. If you do, CI cannot access the repository." Use **Warning** for data loss, security or irreversible actions. Use **Caution** for a failure that you can repair. |
| 8 | Punctuation and word counts | Do not use semicolons. Use a colon only before a list or a definition. Use parentheses only for short data, not for a second thought. Count code, commands and identifiers as one word. |
| 9 | Writing practices | Write for the reader who acts next. Be specific: give the file, the number, the command. Make a statement only if you know it is true; if you did not verify it, say so in a separate sentence. |

## Example

Before (baseline, no skill):

> **Order matters**
> Do not delete the old key before you've verified the new one. If the webhook isn't updated,
> staging silently stops syncing.

After:

> **Warning:** Do not delete the old key before the `deploy-staging` workflow passes with the
> new key. If you do, CI cannot access the repository until a person adds a key manually.
>
> 1. Add the new key to the CI secret store.
>    **Caution:** Do not push before you do step 2. If you do, the push returns 401.
> 2. Update the secret of the `staging-sync` webhook.
> 3. Start the `deploy-staging` workflow.
>
> If the webhook secret is old, each push returns 401. Staging does not sync, and no alert
> occurs.

## Gotchas

- **Shorter is not simpler.** Removing articles and verbs makes a message ambiguous. STE removes
  extra words (filler, hedges, pleasantries), not grammar words. When a user instruction asks
  for terse fragments, the user instruction wins; keep rules 1, 3, 5 and 7 in the fragments.
- **Warnings drift to the end.** Agents write the procedure first and add the risk last, or add
  a "Caution" section after the steps and point to it. Move each warning or caution into the
  procedure, directly above its step.
- **Quoted text keeps its words.** Do not rewrite an error message, a log line or a user quote
  into STE. Quote it exactly, then explain it in STE.
