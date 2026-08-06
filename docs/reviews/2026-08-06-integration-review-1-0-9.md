# Integration Review: parallel session — 1.0.9 release + hosted-service PR #55

**Reviewer:** Hermes agent (task `integration-review-1-0-9`, MoE review)
**Date:** 2026-08-06 **Base → HEAD:** `origin/main` @ `1817a80` + PR #55 (`task/extract-hosted-service` @ `fc6eb26`)
**Scope:** quality check after a parallel work session — how the changed points integrate (package-id migration to
`ai-raccoon` 1.0.9, baseline re-pin #54, observability/parity fixes #51, hosted extraction service #55), regression
check, full suite, manual tests, fresh server setup. **Gates:** `dotnet build` (0 warnings) · full `dotnet test` ·
manual fresh-install protocol (published 1.0.9) · fresh local server + MCP round trip · global-tool migration (owner e:)

---

## Gates (measured, orchestrator)

| Gate                                                         | Result                                                                                                                                                                                                                  |
|--------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Build, main HEAD `1817a80`                                   | 0 warnings / 0 errors                                                                                                                                                                                                   |
| Full suite, main HEAD                                        | **1211 passed / 0 failed / 4 skipped** (prev review: 1206/5/4 — the 5 corpus failures cleared by #54 re-pin)                                                                                                            |
| Build, main + PR #55 merged                                  | 0 warnings / 0 errors                                                                                                                                                                                                   |
| Full suite, main + PR #55                                    | **1225 passed / 0 failed / 4 skipped** (+14 from #55)                                                                                                                                                                   |
| Fresh local server (fresh bank, merged build)                | extract list/enable/mode round trip ok; invalid mode → exit 1; MCP write→search→stats ok; memory_share_extract propose+promote no crash, valid shapes                                                                   |
| Manual fresh-install protocol (published `ai-raccoon` 1.0.9) | **ALL GREEN** (install first try, layout+sha256, version, stdio round trip pending=0, dual-instance, graceful shutdown)                                                                                                 |
| Global-tool migration (owner e:)                             | `arasz.ai-raccoon` 1.0.8 → `ai-raccoon` 1.0.9 installed; `--version` = 1.0.9+d7bd782; bank `~/.ai-raccoon/memory.db` intact (23 MB); stdio round trip on real bank: 20 tools, memory_stats shows all 4 project contexts |
| NuGet registration                                           | 6/6 matrix RIDs serve 200 (osx-x64/linux-musl-arm64 not in matrix — expected 404s)                                                                                                                                      |

## Findings

<!-- filled from the 4 expert lanes (architect, code-reviewer, test-engineer, dotnet-engineer) -->

## Verdict

<!-- integrated verdict + owner-gate decisions -->

## Post-merge checklist (owner)

- [ ] Merge PR #55 (hosted extraction service)
- [ ] Version bump to 1.0.10 (owner f:)
- [ ] Re-run fresh-install protocol after the next publish (`AI_RACCOON_VERSION=<new>`)
