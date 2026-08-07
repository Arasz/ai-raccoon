# work/

Dated work records: plans, designs, research, reviews, incidents, backlog. Every file is
named `YYYY-MM-DD-slug.md` and is a snapshot — it records what was true when it was
written, not what is true now.

## Contents

Active records:

| File | What it is |
|---|---|
| [2026-08-07-skill-discovery-mcp-server.md](2026-08-07-skill-discovery-mcp-server.md) | Active research, not yet implemented |

Frozen, build-embedded Gherkin bundles — referenced by
`tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj`'s `ReqnrollFeatureFiles`; never move,
rename, or reformat these paths:

| Bundle | What it is |
|---|---|
| [features-agent-memory/](features-agent-memory/) | `agent-memory.feature` (29 scenarios) + `spec-issue-1.md` design rationale, linked from `docs/reference/agent-memory-server.md` |
| [features-native-memory/](features-native-memory/) | `native-memory.feature` + `spec.json` native-store scope |

`archive/` holds superseded sprint records — implemented plans, research whose
conclusions now live in an ADR or reference doc, and reports whose verdicts already
shipped. Kept for provenance (some are cited directly as ADR evidence trails), not for
day-to-day reading; consult the relevant ADR or `docs/reference/`/`docs/explanation/`
doc first.
