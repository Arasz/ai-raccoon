---
name: whats-new-update
description: Use when updating README "What's new" after a release. Compact, braggable-features-only list.
---

# README "What's new" update

Rules for the `## What's new` section in `README.md` (packed into the NuGet package readme, so it shows on nuget.org).

## What belongs

- **Only user-facing features worth bragging about.** The test: "can the user really feel this change?" — a new capability, a big speed win, a workflow that stopped being painful. Not internal refactors, not bug fixes, not dependency bumps.
- **Compact.** Each entry is a bold title + a doc/ADR reference link, nothing else. No prose, no justification.

## Shape

```markdown
## What's new

- **Extensible FileType Handlers & Native JSON Support (1.8.0).** [ADR-0027](docs/adr/0027-extensible-file-type-handlers-and-json-support.md)
- **Connecting a client is all it takes.** [ADR-0020](docs/adr/0020-always-on-http-stdio-proxy.md)
```

- Newest at the top. The version goes in the bold title only when the entry shipped under that version.
- Link an ADR when one exists; otherwise a plan / how-to / reference doc.
- A patch release with no braggable feature gets no entry.
