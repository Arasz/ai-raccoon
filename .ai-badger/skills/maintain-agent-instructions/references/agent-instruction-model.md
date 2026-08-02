# Agent instruction model reference

The model lives at `.ai-badger/agent-instructions/model.json` by default (override with the
`AGENT_INSTRUCTIONS_DIR` environment variable if a project places it elsewhere) and is validated
against the sibling `schema.json` in the same directory. The agnostic schema shape and a blank
starting skeleton ship with the framework at `common/templates/agent-instructions/` — see that
directory's `README.md` for how a project's `model.json` gets filled in.

## Sections

- `files`: required instruction files and token/line-count budgets.
- `directories`: required directories and allowed file names.
- `instructionSets`: path-scoped instruction files and required topics.
- `sharedPolicy.authoritativeDocs`: spec documents that agents should treat as authoritative.
- `sharedPolicy.nonNegotiableInvariants`: project invariants that must appear in specified agent files.
- `sharedPolicy.reviewCategories`: review categories that must stay aligned between Copilot review instructions and any hosted-review severity-bar doc.
- `agents`: each supported agent entrypoint and role.
- `validation`: required headings, optional heading metadata, and explicit required/forbidden patterns.

## Heading metadata

Headings should stay simple and human-readable. If a section needs machine-readable metadata, add an `agent-section` HTML comment immediately after the heading:

```markdown
## Review priorities

<!-- agent-section: {"ordered": true} -->
```

In `model.json`, required headings may be strings or objects:

```json
{ "text": "Review priorities", "metadata": { "ordered": true } }
```

## Pattern semantics

Patterns are JavaScript regular expressions evaluated case-insensitively and with dot-all mode.

For a topic or invariant with multiple patterns, at least one pattern must match unless the script documents a stricter rule.

## Maintenance rule

When shared policy changes, update `model.json` first, then update instruction files, then run validation and drift checks.
