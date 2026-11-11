# MADR 4 minimal template

The fallback for a project with no decision log. Source: MADR 4.0.0 (https://adr.github.io/madr/,
"Full template"), reduced to its required sections plus Consequences and Confirmation. Save as
`docs/decisions/NNNN-title-with-dashes.md`.

```markdown
---
status: proposed
date: YYYY-MM-DD
decision-makers: <who decided>
---

# <short title: the problem solved and the solution chosen>

## Context and Problem Statement

<Two or three sentences, or a question. Link the issue or discussion that raised it.>

## Considered Options

* <option 1>
* <option 2>
* <doing nothing>

## Decision Outcome

Chosen option: "<option>", because <the driver it satisfies that the others do not>.

### Consequences

* Good, because <what improves>
* Bad, because <what it costs or forecloses>

### Confirmation

<How compliance is checked: a test, a gate, a review question.>
```
