# Lane report — CI, tooling, Python scripts and documentation

Campaign: project-scope review, base `1d1889d517baf840df0b839f547091bd7f46808b`.
Model: sonnet · read-only. Lane verified the base SHA and re-ran `dotnet build` (0/0) in its worktree.

---

### F1 — `dotnet test --list-tests` undercounts the CI-gating `Speed=Fast` partition by exactly 20 tests, making it unsafe as a coverage-verification shortcut [MEASURED]
**Severity:** LOW
**Evidence:** `--filter "Speed=Fast" --list-tests` → 2122 lines; the actual run → `Passed: 2141,
Skipped: 1, Total: 2142`. The same 20-test gap reproduces unfiltered: `--list-tests` → 2850 against
2870 executed. `Category=bdd` (143) and `Speed=Slow` (585) matched exactly — the discrepancy is
isolated to `Speed=Fast`, most likely a data-driven `[Theory]` whose case count differs between
discovery and execution.

**Why it matters:** the campaign brief itself suggested `--list-tests` as the cheap way to verify
partition coverage. It is not reliable for that here. Only an actual run proves it, which the lane did:

```
Speed=Fast   : 2141 passed + 1 skipped = 2142
Category=bdd :  138 passed + 5 skipped =  143
Speed=Slow   :  582 passed + 3 skipped =  585
                                  sum  = 2870
```

matching the unfiltered run's 2861 + 0 + 9 = 2870 exactly. **No escapee — the partition is exact.**
*(Independently reproduced by the test-QA lane, which reached the identical three numbers by a
different route.)*

**Fix:** none needed for CI correctness. Note in the test-suite docs that `--list-tests` is not a
reliable substitute for a real filtered run.

---

### F2 — No workflow caches NuGet restore, and no `packages.lock.json` exists to make a cache key correct if one were added [MEASURED]
**Severity:** LOW
**Evidence:** `grep -rn "cache" .github/workflows/` → zero hits.
`find . -maxdepth 3 -name packages.lock.json` → none; no `RestorePackagesWithLockFile` in
`Directory.Build.props`. Every job in `build.yml` (×3), `nightly.yml`, and `publish.yml` (×6 RID matrix
legs) runs a cold restore.

**Why it matters:** the trap the brief warns about, in reverse. Floating package version ranges with no
lock file mean a naive `actions/cache` keyed on `hashFiles('**/*.csproj')` would silently serve stale
packages across a version-range bump — worse than no cache. Any future caching work must land lock
files first.

**Fix:** if restore time becomes a bottleneck, add `packages.lock.json` +
`RestorePackagesWithLockFile=true`, then cache keyed on that file's hash.

---

### F3 — CI builds and tests on `ubuntu-latest` only, while `publish.yml` ships native packages for six RIDs including Windows and macOS, and ADR-0049 already proves platform changes retrieval output [READ]
**Severity:** HIGH
**Evidence:** `build.yml`/`nightly.yml`: every job is `runs-on: ubuntu-latest`, no OS matrix.
`publish.yml:40-41`: `matrix: rid: [win-x64, win-arm64, osx-arm64, linux-x64, linux-arm64,
linux-musl-x64]`. `docs/adr/0049-embeddings-depend-on-the-host-cpu.md`: *"Six identical CI jobs from one
commit split exactly on `avx512_vnni`: VNNI hosts measure AdrNdcg5 0.4886, non-VNNI x64 hosts 0.5588,
macOS arm64 0.5261 — a 0.070 spread against a 5e-3 tolerance."*
`docs/adr/0050-sweep-gates-search-with-pinned-query-vectors.md` documents the workaround this forced:
pinning query vectors in a fixture because "no Linux runner produces" the number the gate needs, rather
than actually testing the shipped platforms.

**Why it matters:** not hypothetical — the project's own ADR measured a real 0.070 nDCG spread from host
CPU alone, on a product shipping six RID-specific packages people `dotnet tool install` directly. CI
never builds or tests on the platforms it publishes to, so a Windows- or macOS-specific regression (not
just a CPU-microarchitecture one) has no PR gate before nuget.org.

**Fix:** add at minimum a `macos-latest` and `windows-latest` leg to `build-fast` — not all three jobs,
for cost control.

---

### F4 — `nightly.yml` failures have no automated notification; visibility depends entirely on someone manually running `gh run list` [MEASURED]
**Severity:** MEDIUM
**Evidence:** `nightly.yml` has no Slack/issue/email step; its own top-of-file comment candidly says so
and gives the mitigation command. Ran it: `gh run list --workflow=nightly.yml --limit 10` → failures on
2026-08-06, 07, 08 (×2), 10, 12; successes on 09, 11, 13, 14. The 2026-08-12 failure has no visible
follow-up commit or issue.

**Why it matters:** the file is honest about the risk it accepts, but the risk is live — a nightly-only
regression can go unnoticed indefinitely without someone remembering to poll.

**Fix:** an `if: failure()` step posting to an issue or an existing notification channel.

---

### F5 — Python scripts are not a CI gate: no workflow ever invokes `pytest`, leaving 182 test functions across 46 files unverified by CI [MEASURED]
**Severity:** MEDIUM
**Evidence:** `grep -rn "pytest\|verify-tool-package" .github/workflows/*.yml` → only path-filter
*trigger* references to `scripts/verify-tool-package.py` in `build.yml`'s `paths:` list (`:16,29`) — the
script is never *executed* as a step anywhere. `grep -c "def test_" scripts/tests/*.py
integrations/hermes/tests/*.py` → **182**. `pyproject.toml`: `testpaths = ["scripts/tests"]` — which does
not even include `integrations/hermes/tests`. No `setup-python`/`setup-uv` action in any workflow.

**Why it matters:** confirms the recorded owner decision that Python tests are out of CI scope — but
formally establishing it also surfaces that the ungated scope is larger than "scripts": it includes the
Hermes integration package's tests, **and `scripts/verify-tool-package.py`, which `publish.yml`'s own
comment calls the pack job's only pre-publish integrity check, is never run automatically anywhere.**

**Fix:** none required if this is an accepted documented boundary. If `verify-tool-package.py` is
load-bearing for release integrity, it should run as an actual step, not just gate a trigger path.

---

### F6 — `pyproject.toml` declares zero dependencies while scripts import third-party packages that `uv.lock` never locks [MEASURED]
**Severity:** MEDIUM
**Evidence:** `pyproject.toml` has no `[project.dependencies]` key at all. `uv.lock` is **8 lines**
total (only the local `ai-raccoon-scripts` package, no third-party entries).
`scripts/train-structural-noise-model.py:31-34` — `import numpy as np`, `from sklearn.ensemble import
HistGradientBoostingClassifier`. `scripts/src/pipeline.py:18` and `scripts/src/mcp_client.py:13` —
`import httpx`.

**Why it matters:** `uv sync`/`uv run` on a clean checkout cannot provide these packages; the scripts
only work if numpy/scikit-learn/httpx happen to be installed globally. The lockfile exists and is
committed but describes an environment that cannot run the code it covers.

**Fix:** add the three packages (and audit for others) to `[project.dependencies]` and regenerate
`uv.lock`.

---

### F7 — Three one-off, version-pinned release-verification scripts (~1,671 lines combined) are unreferenced anywhere — dead code [MEASURED]
**Severity:** LOW
**Evidence:** `scripts/run_1_9_1_live_checklist.py` (425 lines), `scripts/run_full_tool_test_1_9_0.py`
(780), `scripts/run_1_10_1_live_checklist.py` (466). A repo-wide search for their names across docs,
workflows and other scripts → **zero hits**. `run_1_10_1_live_checklist.py:4` self-documents: "Adapted
from run_1_9_1_live_checklist.py for the 1.10.1 release" — a copy-paste fork, not a generalised tool.
Current shipped version is 1.12.0; none was updated for 1.11.0 or 1.12.0.

**Fix:** delete all three, or generalise one script parameterised by version instead of forking a file
per release.

---

### F8 — Two scripts hardcode the owner's absolute developer-machine paths, so the committed benchmark corpus can only be regenerated on that one machine [MEASURED]
**Severity:** LOW
**Evidence:** `scripts/src/benchmark_corpus.py:19-24` — `REPOS = {"jsaa":
"/Users/arasz/RiderProjects/job-search-ai-assistant", "badger": "…/ai-badger", "home":
"…/arasz-home-page"}`, `OUT = "/Users/arasz/RiderProjects/ai-raccoon/benchmarks/…/Corpus"`.
`scripts/src/jsaa_config.py:8` — `JSAA_ROOT = Path("/Users/arasz/RiderProjects/job-search-ai-assistant")`.

**Why it matters:** a manual, non-CI generator — but it means the benchmark corpus consumed by the
retrieval gates is effectively unregeneratable by anyone but the owner, on this exact machine layout,
with no error message pointing at that fact.

**Fix:** read the sibling-repo roots from an env var or a config file with a documented default.

---

### F9 — `identifier.sqlite` is a 0-byte file accidentally committed to git and never gitignored [MEASURED]
**Severity:** LOW
**Evidence:** tracked per `git ls-files`. `git show 11df8313 --stat -- identifier.sqlite` → `0` bytes,
added in "refactor: use primary constructors and ArgumentException.ThrowIfNullOrWhiteSpace" — an
unrelated C# refactor. `git log --all --oneline -- identifier.sqlite` → exactly one commit ever.
`.gitignore` has no matching rule (only `*.db-shm`/`*.db-wal`).

**Fix:** `git rm identifier.sqlite` and add a rule.

---

### F10 — GitHub's native release notes for v1.11.0 list 1 of the 21 commits it shipped, because ~20 commits were pushed directly to `main`, bypassing the project's own PR-per-task invariant [MEASURED]
**Severity:** HIGH
**Evidence:** `gh release view v1.11.0 --json body` → **one bullet**
(`fix(serve): only warn about ignored --transport when explicitly set`, PR #275).
`git log --oneline v1.10.0..v1.11.0` → **21 commits**.
`gh pr list --state merged --search "merged:2026-08-13..2026-08-14"` → only PR #275 in range.
Spot-check: `gh api repos/Arasz/ai-raccoon/commits/9e2d9b5…/pulls` → `[]` for
`fix(sqlite): wait up to 5s for the write lock instead of failing SQLITE_BUSY` — no PR at all. The
window also includes a noise-vector recalibration and a semantic-classifier removal, neither visible in
GitHub's release notes.

**Why it matters:** CLAUDE.md's invariant states "Every unit of work ends in a pull request; never push
directly to the main/trunk branch." This 21-commit window shows it was not followed for one
release-crunch day, and the consequence is concrete: GitHub's release-notes mechanism — the traceability
surface external tools and users trust — silently under-describes what v1.11.0 contains. (README.md's
own "What's new" changelog *does* cover the substance correctly — see Healthy.)

**Fix:** not a docs fix — either route these through PRs going forward, or record the fast-iteration
exception explicitly rather than leaving it as silent divergence from a stated invariant.

---

### F11 — ADR-0029, ADR-0030 and ADR-0013 still say `Status: Accepted` in their own files despite being superseded or reversed; only the index records it [MEASURED]
**Severity:** MEDIUM · **Class:** verifiably-false (the `Status:` field specifically)
**Evidence:** `docs/adr/0029-pre-write-noise-filtering.md:6` and
`docs/adr/0030-realtime-heuristic-ttl.md:6` both read `Accepted`, with zero occurrences of
"superseded"/"reversed" in either file; `docs/adr/0013-*.md:5` the same. Compare
`docs/adr/0002-opentelemetry-observability.md:5-10`, which self-updates:
`Status: **Superseded** — 2026-08-09. Superseded in parts by ADR 0008… 0009… 0021`.
`docs/adr/README.md:39-40` (the index) **does** correctly record that 0029 is "superseded in part by
ADR-0033 and ADR-0039" and 0030 is "reversed by ADR-0034" — the aggregate index is accurate; only the
three source files lag.

**Why it matters:** `docs/adr/README.md:3-4` calls ADRs "immutable, frozen". A reader who opens 0029 or
0030 directly sees a live-sounding decision describing a filter and a TTL policy that no longer exist in
`src/`, with no forward pointer. The project already has the correct pattern; it was not applied here.

**Fix:** add the same one-line `Status: Superseded/Reversed — <date>. See ADR-00XX.` to all three.

---

### F12 — One local git branch holds a commit reachable from no origin ref and not an ancestor of `main` — the exact "lost lane output" pattern, though this instance's content already landed independently [MEASURED]
**Severity:** LOW
**Evidence:** `git worktree list` → **49 worktrees**, **6 marked `prunable`**. `git branch -r` → **108**
remote branches; `git branch -r --no-merged origin/main` → **103** unmerged.
`git for-each-ref … refs/heads/ | awk '$2==""'` → 4 local branches with no upstream; of those,
`work/section-ab-measure` (tip `4a2640c8`) has no matching `origin/` ref at all and
`git merge-base --is-ancestor 4a2640c8 main` → **not an ancestor**. Its *content* (backfilling the ADR
index through 0050) already exists on `main` via a different commit — nothing was lost, but confirming
that required manual git archaeology.

**Fix:** `git branch -d work/section-ab-measure` and a periodic sweep of prunable worktrees and
upstream-less branches.

---

### F13 — `docs/reference/agent-memory-server.md`'s tool table omits `memory_promotion_list`'s `includeFullValue` parameter [MEASURED]
**Severity:** LOW · **Class:** verifiably-false (incomplete; the live MCP schema is correct)
**Evidence:** doc `:58` lists only `projectId?`, `limit=50`. Code
`tests/AiRaccoon.Tests/Unit/Mcp/McpToolContractTests.cs:35` —
`memory_promotion_list(projectId:string|null?, limit:integer?, includeFullValue:boolean?)`.
`grep -n "includeFullValue" docs/reference/agent-memory-server.md` → zero hits.

**Why it matters:** this parameter is the one existing way an agent can read a memory's full content
rather than a truncated snippet — exactly the read-path gap the prior review's B2 was about — and the
doc that calls itself the "complete tool contract" does not mention it. *(Independently found by the
consumer-surface lane as its F6.)*

---

### F14 — README.md's architecture tree says the test suite has "1100+ tests", undercounting by roughly 2.6× [READ]
**Severity:** LOW · **Class:** ambiguous (true as a floor, materially stale as a scale indicator)
**Evidence:** `README.md:169` — `tests/AiRaccoon.Tests/ # xunit.v3 test suite (1100+ tests)`. Actual:
2,850 discovered / 2,870 executed. `git log -1 --format=%ai -- README.md` → edited today, so this is a
line nobody updates as the suite grows.

---

### F15 — `--transport https` is presented as an equal choice in the how-to doc, while the CLI's own `--help` calls it unsupported, yet the code wires it up [READ]
**Severity:** LOW · **Class:** ambiguous (the contradiction originates in the code's help string)
**Evidence:** doc `docs/how-to/configure-ai-raccoon-server.md:22` lists `proxy, stdio, http, https` with
no caveat. Code `src/AiRaccoon/Setup/Cli/CliCommandTree.cs:98` — the option's own description says
`"…https unsupported"`. Yet `McpServerSetup.cs:113` and `AppRegistrations.cs:245` show `Https` **is**
implemented and dispatched.

**Fix:** not primarily a docs fix — the help string itself is stale; the how-to inherited it.

---

## Still open

- Whether the ADR `Status:` gap (F11) exists on superseded ADRs beyond 0013/0029/0030 — not exhaustively swept across all 50+.
- ADR-0040 through 0050's underlying benchmark figures were read, not independently re-measured.
- Whether the 2026-08-12 silent nightly failure (F4) was ever noticed or acted on.
- A full script-by-script dead-code sweep of all 46 Python files — only the three checklist scripts were traced individually.
- Whether the direct-to-main pattern (F10) recurs at other release boundaries beyond the sampled v1.10.0→v1.11.0 window.
- Whether GitHub still lists the now-deleted `ndcg-probe` workflow in the Actions UI — cosmetic clutter, not a live fifth workflow.

## Grade mix

MEASURED 12 · READ 3 (F3, F14, F15) · INFERRED 0 · UNVERIFIED 0.

## Owner questions

1. Should the 182 Python test functions become an actual CI gate, or is "dev-only, owner-run" the intended permanent scope?
2. Is the ~20-commit direct-to-main window around v1.10.0→v1.11.0 an accepted fast-iteration exception, or enforced going forward?
3. Is a macOS/Windows CI leg worth the cost given ADR-0049 already proves platform-dependent output, or is Linux-only intentional because users install prebuilt RID packages?
4. Should NuGet restore caching be added (needs `packages.lock.json` first), or is the current ~3× redundant cold restore acceptable?
5. Is the `production` environment's single required reviewer (only the owner, who can also admin-bypass) meant as a real gate or an accepted formality?

## Healthy

- **SHA-pinning still holds project-wide**: `grep -rn "uses:" .github/workflows/ | grep -v "@[0-9a-f]\{40\}"` → empty, re-verified today.
- **CI's three trait filters partition the suite exactly, proven by actual execution** (not just discovery): 2142 + 143 + 585 = 2870, matching 2861 + 0 + 9 precisely. No escapee.
- **`labeler.yml`'s job permissions** (`contents: read`, `pull-requests: write`) are the minimum `actions/labeler@v7` needs.
- **`publish.yml` uses OIDC trusted publishing** (no stored API key); the `production` environment does have a configured required-reviewer rule.
- **Every Python `subprocess` call reviewed uses list-form arguments** — no `shell=True`, no string-interpolated commands, no injection risk found.
- **`dist/`, `.nupkg-local/` and `semantica.log` are all correctly gitignored and not tracked** — only `identifier.sqlite` is a genuine tracked stray (F9).
- **`tests/AiRaccoon.Tests/Unit/Docs/AdrIndexTests.cs` is a real derived guard** (compares disk against the index, catching missing/stale/gap/false-skip) — not a hand-maintained list that can silently drift.
- **README.md's "What's new" is a genuine, dated, self-correcting changelog** covering v1.10.0 through v1.12.0 — release traceability is satisfied by this document even where GitHub's native release notes are not (F10).
- **The version-bump contract fails safe:** `AiRaccoon.csproj`, `.mcp/server.json` (×2 fields) and `VersionContractTests.cs` mean a forgotten bump breaks the build rather than silently shipping a mismatch.
- **The prior MoE review's B1/WP1/WP2 findings were genuinely resolved** in the 52 commits since, via ADR-0032/0033/0034 — `memory_get` exists, the noise filter and dead auto-TTL policy were deleted, write outcomes are honest.

## Disconfirmed

- **Root artifacts might contain committed data.** Disconfirmed — `dist/`, `.nupkg-local`, `semantica.log` are none of them tracked; only the empty `identifier.sqlite` is.
- **49 worktrees / 108 remote branches represent widespread unpushed or lost work.** Largely disconfirmed by spot-checking — nearly every local branch has a matching, up-to-date origin branch. One truly local-only branch was found (F12), and even its content had already landed on `main`.
- **ADR-0025/0029/0030 are currently-open blockers** (the prior review's characterisation). Disconfirmed as still open; resolved same-day via ADR-0032/0033/0034 and shipped in v1.12.0.
- **The CI trait partition may have drifted after 52 commits of test-suite churn** (previously 1658+142+483 = 2283). Disconfirmed — re-measured today at 2142+143+585 = 2870, still exact.
- **Python `subprocess` calls are built from unsanitised input.** Disconfirmed — all reviewed sites use list-form args.
