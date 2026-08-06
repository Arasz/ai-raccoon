# Shipped-surface PR workflow in the ai-badger repo (verified 2026-08-06, PR #316)

Any edit to `features/common/mcp/<server>/server.md` or `meta.json` is a
**shipped-surface** change: server.md scaffolds VERBATIM into every agent file
(CLAUDE.md, HERMES.md, .hermes.md, .ai-badger/CLAUDE.md, copilot-instructions
copies), and meta.json is the catalog. Two local gates fire on such a PR and both
must be satisfied in order. Worktree isolation: create a worktree from origin/main
(`git worktree add <path> origin/main`) — the main checkout belongs to other
sessions — and run tests with the MAIN checkout's venv python
(`/Users/arasz/RiderProjects/ai-badger/.venv/bin/python3`; worktrees have no
.venv). The `pre-commit` binary is NOT on PATH: the repo's `.git/hooks/pre-commit`
template runs it via `INSTALL_PYTHON=/Library/Developer/CommandLineTools/usr/bin/python3
-m pre_commit`.

## Gate 1 — scaffold-freshness-guard (pre-commit) demands a re-scaffold

After editing server.md, the pre-commit `scaffold-freshness-guard` fails with
"re-scaffolding this repo against itself would change N path(s)" listing the 7
agent files ("content differs, regenerates differently"). This is NOT pre-existing
drift — the guard passes on clean origin/main; your server.md change is the cause
(clean-tree check: `git stash -u` → run the hook → `git stash pop`).

Fix, exactly per the guard's message:

```bash
python3 features/common/skills/welcome-ai-badger/scripts/scaffold.py \
    --config .ai-badger/config.json --target . --root . --no-install --skills ''
```

Then RESTORE two environmental artifacts the scaffold rewrites:
- `.ai-badger/manifest.json` — its `frameworkCommit` is regenerated to the
  current HEAD; not caused by your change, not part of your diff
  (`git checkout -- .ai-badger/manifest.json`).
- `.github/mcp.json` — the scaffold DROPS hermes/ai-raccoon when the PATH probe
  fails in your shell (PATH-dependent by design; CI uses
  `AI_BADGER_MCP_AVAILABILITY=all`). Restore it or your PR removes servers
  (`git checkout -- .github/mcp.json`).

Commit the 7 regenerated agent files WITH your change — that is the sanctioned
integration, not collateral.

## Gate 2 — the 15-line server.md budget

`tests/test_mcp_catalog_instructions.py::test_every_shipped_server_md_stays_within_the_line_budget`
asserts every `features/*/mcp/*/server.md` ≤ 15 lines (`strip().splitlines()`).
Running only `test_common_ai_raccoon_mcp_server.py` (the obvious focused file)
does NOT cover it — the full pre-push pytest lane catches it. Budget arithmetic
bites: an original 15-line file has 2 blank lines + 13 content lines; replacing
2 tail lines with 3 new ones is +1 → 16 → FAIL. Merge tail lines (e.g. fold the
new guidance into the existing "One-time CLI setup" line) and count
programmatically before re-scaffolding.

## Gate 3 — the release lane: shipped surface ⇒ release-shaped PR

The pre-push `.lefthook/pre-push/verify.sh` `release` lane fails when the shipped
surface changed since the last tag and VERSION didn't move:
"shipped surface changed since ai-badger--v0.81.0 but VERSION is still 0.81.0".
Per RELEASING.md's "Semver for a catalog": 0.MINOR for anything that changes what
scaffolding does to a consumer repo (server.md content qualifies). Full sequence:

```bash
echo "0.82.0" > VERSION
# docs/changelog/{version}-{slug}.md — what changed + scaffold impact
<venv-python> tooling/changelog_index.py      # regenerates docs/changelog/README.md
<venv-python> tooling/version_sync.py         # propagates to plugin.json, marketplace.json, index.json
<venv-python> tooling/version_sync.py --check && <venv-python> tooling/changelog_index.py --check
<venv-python> gates/release_guard.py          # prints "shipped surface changed ... VERSION was bumped — PASS"
```

The tag is automatic post-merge (workflow tags `ai-badger--v{version}`); nobody
runs `claude plugin tag --push` by hand.

## Gate 4 — pre-push quality gate (the full suite runs on EVERY push)

`.lefthook/pre-push/verify.sh` runs ~9 lanes including a FULL pytest (~2 min,
3200+ tests) and the release lane — budget ~3 min per push. Failure output gives
the reproduce command (`.lefthook/pre-push/verify.sh <lane>`) and per-lane
`VERIFY_SKIP=<lane>` bypasses. The pytest lane is what caught the 15-line budget
violation the focused file missed — treat it as the canonical gate, not a formality.

## Worked example (PR #316, ai-raccoon HTTP default, 0.81.0 → 0.82.0)

Sequence: edit meta.json + server.md (+ a contract test) → commit blocked by
scaffold-freshness-guard → re-scaffold → restore manifest.json + .github/mcp.json
→ guard passes → push blocked by pytest lane (server.md 22 lines > 15) → rewrite
to 15 lines, re-scaffold, restore artifacts again → push blocked by release lane →
VERSION 0.82.0 + changelog + version_sync + release_guard → all lanes green → PR
opened. Total: 3 commits (the catalog change, the budget fix, the release bump).
