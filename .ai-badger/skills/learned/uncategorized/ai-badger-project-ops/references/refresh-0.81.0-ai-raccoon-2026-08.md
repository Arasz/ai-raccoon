# Refresh 0.81.0 → ai-raccoon, 2026-08-06 (config-only: python stack add)

Worked example: same framework version (0.81.0 == 0.81.0 == 0.81.0), yet a
re-scaffold ran — the drift was CONFIG, not the framework.

## The config-only drift shape

config.json had `"stacks": ["dotnet", "mcp", "python"]` but the last scaffold
predated the `python` entry: the manifest had ZERO python entries and every
generated agent file said "Stacks: dotnet, mcp". The report showed:

- `drift.configChanged` — recorded config hash vs current hash (the stacks edit)
- `drift.newItems` — `python.instructions` (features/python/instructions/...)
- `reScaffolded: true`, 141 entries, no version change

Lesson: a stack ADD to `config.stacks` is itself the drift. No version bump
needed; the re-scaffold delivers the stack's items (instructions, skills-data,
personas). The generated files then read "Stacks: dotnet, mcp, python".

## index_build.py output location

`tooling/index_build.py` prints "wrote index.json — 19 stacks, 111 feature
items" but writes to the FRAMEWORK ROOT (`$AI_BADGER/index.json`), NOT
`tooling/index.json`. `ls tooling/index.json` fails immediately after a
successful run — look at the root. (den-refresh's skill text says "run
index_build.py first" without naming the location.)

## Locally-modified skill overwrite was a FIX, not a loss

`drift.locallyModified` listed `features/common/skills/ai-raccoon-memory` — the
project had edited its SKILL.md to say `dotnet tool update -g arasz.ai-raccoon`.
The re-scaffold overwrote it with the framework's current text
(`-g ai-raccoon`), which is CORRECT post the nuget-id migration. Diff the
overwritten file against what the project had before assuming content loss —
stale local edits get fixed by the refresh.

## Misc observed

- `.ai-badger.bckp/` created although `breakingChange.isBreaking: false` —
  untracked noise; safe to remove after the commit (new state is in git).
- Framework copies: 19 stale versions under ~/.claude/plugins/cache — report
  only (Claude-owned).
- skillUsage: no invocation channel on this host → everything `cannotTell`,
  nothing to prune; mcp-index (94 hook fires) + task (5) confirmed working.
- Staging: commit ONLY the refresh footprint (managed agent files + new
  python.* files); leave task-tracking/*.json and foreign untracked docs alone.
  `git add <deleted-path>` fails when the deletion is already staged — stage
  surviving paths only.
- The refresh-created `index.json` in the framework root is untracked there —
  do not commit it into the framework repo unless running its own chain.
