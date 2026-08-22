# Force-push investigation — task-branch refs rewritten 2026-08-22

**Date:** 2026-08-22 · **Work package:** WP7 of `docs/work/2026-08-22-post-delta-3-plan.md` ·
**Status:** investigation complete; **every mitigation in §5 is a proposal awaiting owner decision
G5.** Nothing here has been applied — no `git config`, no `settings.json`, no ruleset, no `CLAUDE.md`
edit.

Every command below was run on 2026-08-22 from a worktree of `/Users/arasz/RiderProjects/ai-raccoon`,
and its output is what this document reports.

## 1. The six events

`gh api "repos/Arasz/ai-raccoon/activity?activity_type=force_push&per_page=100"` returns 100 events
spanning 2026-08-06 → 2026-08-22, **all under login `Arasz`**
(`--jq '[.[].actor.login] | unique'` → `["Arasz"]`). By day: 08-06 ×1, 08-07 ×12, 08-08 ×22,
08-09 ×20, 08-13 ×6, 08-14 ×4, 08-15 ×12, 08-16 ×3, 08-17 ×3, 08-19 ×5, 08-20 ×1, 08-21 ×5,
**08-22 ×6**. One account runs every lane, so the login identifies nothing.

All six of today's events, with both commits read out of the shared object store:

| # | Time (UTC) | Ref | before → after | Rewrite shape | Agent-attributable? | Evidence command |
|---|---|---|---|---|---|---|
| 1 | 08:54:05Z | `task/pd-s2-project-identity-adr` | `3fa32704` → `d8f3db58` | **Replay.** Same subject, same author date `2026-08-22 10:39:47 +0200`; parent moved `eb4eb70c` → `367add79`; the replacement's committer date is `10:54:05 +0200` = the event second. | **No** — no transcript hit (§2) | `git log -1 --format='%h\|%s\|%ai\|%ci\|%P' 3fa32704` and `… d8f3db58` |
| 2 | 08:54:30Z | `task/pd-s6a-public-docs-corpus` | `60221e11` → `248e078a` | **Replay.** Same subject, same author date `10:49:59 +0200`; parent `6ca7d1d4` → `becb7119`; committer date `10:54:30 +0200` = the event second. | **No** — the one force command any lane issued for this branch was **blocked and never ran** (§2) | `git log -1 --format='%h\|%s\|%ai\|%ci\|%P' 60221e11` and `… 248e078a` |
| 3 | 09:16:41Z | `task/pd-s9-default-code-model` | `f7e73e98` → `097c7e23` | **Reset to an upstream sibling.** Different subject; both share parent `06f25b2b`; `097c7e23` **is** an ancestor of `origin/main` and `f7e73e98` **is not** — the branch's own commit was dropped for a commit already upstream. Committer date `11:15:36 +0200`, 65 s before the push: the target commit already existed. | **No** — no transcript hit | `git merge-base --is-ancestor 097c7e23 origin/main` (exit 0) · `git merge-base --is-ancestor f7e73e98 origin/main` (exit 1) · `git log -1 --format='%h\|%s\|%ai\|%ci\|%P' f7e73e98` and `… 097c7e23` |
| 4 | 11:51:52Z | `task/fix-host-coupled-e2e-tests` | `363adce9` → `f0d174cc` | **Merge flattening.** `363adce9` is a two-parent merge (`b1d9d4c2` + `00d381dd`, "Merge remote-tracking branch 'origin/main' …"); `f0d174cc` has one parent `aeaddaec` and an author date 4 m 26 s *earlier*. A rebase replaced the merge. | **No** — no transcript hit | `git log -1 --format='%h\|%s\|%ai\|%ci\|%P' 363adce9` and `… f0d174cc` · `git log -1 --format='%h\|%s\|%ai' 00d381dd` |
| 5 | 12:54:10Z | `task/release-1-32-0` | `894b1b12` → `a4fc27f0` | **Replay.** Same subject, same author date `14:28:54 +0200`; parent `fe8058ca` → `0eabd002`; committer date `14:54:10 +0200` = the event second. | **No** — no transcript hit | `git log -1 --format='%h\|%s\|%ai\|%ci\|%P' 894b1b12` and `… a4fc27f0` |
| 6 | 12:54:39Z | `task/fix-code-engine-unloadable` | `5b0b52d0` → `12ed394d` | **Replay.** Same subject, same author date `14:49:08 +0200`; parent `05d9a08c` → `5695dc17`; committer date `14:54:39 +0200` = the event second. | **No** — no transcript hit | `git log -1 --format='%h\|%s\|%ai\|%ci\|%P' 5b0b52d0` and `… 12ed394d` |

Two observations fall out of the table:

- **Five of six replacement commits were created in the second they were pushed** (rows 1, 2, 4, 5, 6
  — committer date equals the event timestamp). The rewrite and the push are one action, not a rebase
  done earlier and pushed later.
- **Two pairs land seconds apart on different branches**: rows 1 and 2 are 25 s apart, rows 5 and 6
  are 29 s apart. Whatever issued them handled two branches in one go.

## 2. Attribution: none of the six is agent-attributable

`find /Users/arasz/.claude/projects -name '*.jsonl' -newermt 2026-08-22 | wc -l` → **443** transcripts
(sessions and subagents). Scanning every `Bash` `tool_use` in them for
`git\s+push\b[^"']*?(--force-with-lease|--force|\s-f\b)` returns four hits; three are false positives
(two heredocs that write *this* work package's own plan text, one `gh api … -f query=`). **Exactly one
is a real force-push command:**

```
/Users/arasz/.claude/projects/-Users-arasz-RiderProjects-job-search-ai-assistant/
  3f1cc469-31d7-4fcf-9d70-c8fa6929fe81/subagents/agent-a470bff9be181875b.jsonl
2026-08-22T08:50:06.345Z
cd /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/post-delta-next-steps \
  && git push --force-with-lease 2>&1 | tail -5 && echo "=== status ===" && git status --short --branch
```

**That command never ran.** Its tool result, in the same transcript:

```
PreToolUse:Bash hook error: [bash "${CLAUDE_PLUGIN_ROOT}/hooks/pre-bash-guard.sh"]:
BLOCKED: Force push detected. Use regular push or discuss with the user first.
```

The lane then reached the same end state without a force push: at `08:51:00.585Z` it ran
`git rebase --onto 6ca7d1d4 9e3fe811 task/pd-s6a-public-docs-corpus`, producing `60221e11`, and at
`08:51:03.144Z` a plain `git push` fast-forwarded the remote — `6ca7d1d4..60221e11`, no force. The
08:54:30Z rewrite of that same branch (row 2) happened **3.5 minutes later**, while that lane was
running pytest (its commands at `08:54:22.947Z` and `08:54:38.523Z` are pytest invocations; neither
touches git).

Grepping the same 443 transcripts for each of the six branch names alongside a force flag returns
**zero** real hits — every apparent hit is one of the two heredocs writing this WP's own plan text.

**This corrects the WP7 draft in the plan**, which recorded the `--force-with-lease` above as matching
the 08:54:30Z event. It does not: it was blocked by a hook, and the branch's own lane fast-forwarded
instead. **All six events are unattributed.**

Scans run, all over transcripts modified today:

```bash
find /Users/arasz/.claude/projects -name '*.jsonl' -newermt 2026-08-22 | wc -l   # -> 443
# force-push scan, over every Bash tool_use command string:
#   git\s+push\b[^"']*?(--force-with-lease|--force|\s-f\b)          -> 4 hits, 1 real, 0 executed
# per-branch scan: the same regex, additionally requiring one of the six branch names
#                                                                   -> 0 real hits
# git-pull scan:   \bgit\s+pull\b                                    -> 20 invocations
# git-rebase scan: \bgit\s+rebase\b                                  -> 18 invocations
```

## 3. Mechanism — what made a history rewrite the reachable path

**`pull.rebase` is true globally, and nothing overrides it in this repo.**

```bash
git config --global --list | grep -i pull     # -> pull.rebase=true
git config --local --get-regexp '^pull\.'     # -> no output (no repo-local override)
git config --get core.hooksPath               # -> unset
ls -1 /Users/arasz/RiderProjects/ai-raccoon/.git/hooks/ | grep -v '\.sample$'   # -> no non-sample hooks
```

Under that setting a bare `git pull` replays local commits, which is exactly the "same subject, same
author date, new parent" shape of rows 1, 2, 5 and 6.

**But no agent triggered it that way today.** All 20 `git pull` invocations in today's transcripts
pass `--no-rebase` or `--ff-only`, neutralising `pull.rebase` in every case; the 18 `git rebase`
invocations are explicit. `pull.rebase=true` is a loaded gun, not the trigger that fired here — worth
disarming, but it does not by itself explain these six events.

**The `soft_deny` guardrail entry cannot match anything.** `~/.claude/settings.json`:

```json
"autoMode": { "soft_deny": ["$defaults", "Bash(git push:* --force)"] }
```

Per the Claude Code permissions reference (`https://code.claude.com/docs/en/permissions`, read today):

> The `:*` suffix is an equivalent way to write a trailing wildcard, so `Bash(ls:*)` matches the same
> commands as `Bash(ls *)`. … **The `:*` form is only recognized at the end of a pattern. In a pattern
> like `Bash(git:* push)`, the colon is treated as a literal character and won't match git commands.**

That entry puts `:*` mid-pattern, so the colon is literal: the rule can only match a command whose
text begins `git push:`, which no command does. It matches **nothing** — not `--force-with-lease`, not
`-f`, and not the plain `--force` it was written for.

**What actually stopped the one agent force push is a plugin hook, not `settings.json`.**

```bash
sed -n '34,37p' /Users/arasz/.claude/plugins/marketplaces/dotnet-claude-kit/hooks/pre-bash-guard.sh
```

```sh
if echo "$COMMAND" | grep -qE 'git\s+push\s+.*--force|git\s+push\s+-f\b'; then
  echo "BLOCKED: Force push detected. Use regular push or discuss with the user first." >&2
  exit 2
fi
```

The plugin is enabled user-wide (`enabledPlugins["dotnet-claude-kit@dotnet-claude-kit"] = true`), which
is why it fired for a lane whose session cwd was a different repository. Two properties of this guard
matter for §5: it greps the whole command string, so it also blocks commands that merely *contain* the
text (writing this document by heredoc tripped it), and it is a plugin file — it disappears if the
plugin is disabled or the marketplace entry moves.

**The AWM hook covers the force forms but arms per directory.** Its regex
(`~/.claude/skills/auto-wm/hooks/awm_gate.py:51`):

```python
r"\bgit\s+push\b[^|;&]*\s(--force|-f)\b",
```

`(--force|-f)\b` matches `--force-with-lease` too — the `\b` sits between `e` and `-`. But arming is
per directory, and `python3 ~/.claude/skills/auto-wm/scripts/awm.py status` reports `AWM: inactive in
this project.` while listing 21 *other* directories as still active. Five of those 21 are
`.claude/worktrees/agent-*` paths that no longer exist on disk (`ls -ld` on each → `No such file or
directory`), so the registry holds stale entries and says nothing about coverage of a worktree
created after arming. A worktree made after arming is uncovered.

**The resume cron is ruled out.**

```bash
crontab -l    # -> */30 * * * * … resume_cron.py run >> …/resume.log
grep -c "2026-08-22" /Users/arasz/RiderProjects/ai-badger/.ai-badger/task-tracking/resume.log   # -> 0
tail -3 /Users/arasz/RiderProjects/ai-badger/.ai-badger/task-tracking/resume.log  # -> last entries 2026-08-20T11:30
```

## 4. What could not be determined

- **Who or what issued the six unattributed pushes.** The activity API exposes only `activity_type`,
  `actor`, `after`, `before`, `id`, `node_id`, `ref`, `timestamp`
  (`gh api "repos/Arasz/ai-raccoon/activity?activity_type=force_push&per_page=1" --jq '.[0]|keys'`),
  and `actor` carries only the GitHub login — no client, user-agent, IP, or token identity. One
  account runs every lane, so `Arasz` distinguishes nothing.
- **Nothing further is recoverable locally.** All six branches were deleted on merge, and no
  remote-tracking reflog survives for any of them
  (`ls /Users/arasz/RiderProjects/ai-raccoon/.git/logs/refs/remotes/origin/` lists none of the six).
  The commit objects on both sides are still reachable, which is what §1 reads, but the reflog that
  would record *which local repository* moved the ref is gone.
- **Therefore this document names no culprit.** It records that no Claude lane's transcript contains a
  force-push command that ran, that the one attempted was blocked, and that the six events are
  consistent with a rebase-then-force performed outside a Claude lane. Confirming or denying that is
  the owner's call in gate G5.

## 5. Proposed mitigations — owner applies; nothing below has been applied

### 5.1 Disarm the global rebase-on-pull

```bash
git config --global pull.rebase false
# stricter, and the better fit for a "merge, never rebase" rule — a pull that cannot fast-forward
# then fails loudly instead of quietly rewriting:
git config --global pull.ff only
# verify:
git config --global --list | grep -i pull
```

Defence in depth: §3 shows every agent `git pull` today already opted out explicitly.

### 5.2 Fix the permission rules in `~/.claude/settings.json`

Syntax verified today against `https://code.claude.com/docs/en/permissions`: "Bash permission rules
support wildcard matching with `*`. Wildcards can appear at any position in the command, including at
the beginning, middle, or end", and "A single `*` matches any sequence of characters including
spaces". The `:*` form is valid **only** as a trailing wildcard (quoted in §3) — which is what makes
the current entry dead.

Replace the existing `"Bash(git push:* --force)"` with the space-wildcard forms:

```json
"autoMode": {
  "soft_deny": [
    "$defaults",
    "Bash(git push --force*)",
    "Bash(git push *--force*)",
    "Bash(git push -f*)",
    "Bash(git push * -f*)"
  ]
}
```

`soft_deny` governs auto mode only. To block the command in **every** mode, add the same patterns to
the documented `permissions.deny` list, which the docs state is enforced by Claude Code regardless of
permission mode:

```json
"permissions": {
  "deny": [
    "Bash(git push --force*)",
    "Bash(git push *--force*)",
    "Bash(git push -f*)",
    "Bash(git push * -f*)"
  ]
}
```

Why these four: `--force*` covers `--force`, `--force-with-lease` and `--force-if-includes`; the
leading `*--force*` covers a flag placed after a remote or another option
(`git push origin --force-with-lease`, `git push -q --force`); `-f*` and `* -f*` cover the short form
in either position. The trailing `*` also lets each rule match a subcommand carrying a redirect
(`git push --force-with-lease 2>&1`), which matters because the docs state a rule "must match each
subcommand independently" of an `&&`-chained command — the exact shape the blocked command took.

Unlike the plugin hook in §3, these rules are matched against the parsed command rather than grepped
out of the raw string, so they do not fire on a document that merely quotes the flag.

### 5.3 A repository ruleset blocking history rewrites on task branches

List what exists first:

```bash
gh api repos/Arasz/ai-raccoon/rulesets --jq '.[] | [.id, .name, .target, .enforcement] | @tsv'
# -> 20445824	default	branch	active
gh api repos/Arasz/ai-raccoon/rulesets/20445824 \
  --jq '{name,target,enforcement,conditions,rules:[.rules[].type],bypass_actors}'
```

The `default` ruleset covers `~DEFAULT_BRANCH` only and grants `RepositoryRole` 5 (admin) a
`bypass_mode: "always"` bypass. The proposed task-branch ruleset deliberately declares **no bypass
actors** — an admin bypass would make it decorative, since the account that pushes is the admin. It
blocks `non_fast_forward` and **does not** block `deletion`, because branches are deleted on merge.

```bash
cat > ruleset.json <<'EOF'
{
  "name": "task-branches-no-history-rewrite",
  "target": "branch",
  "enforcement": "active",
  "bypass_actors": [],
  "conditions": {
    "ref_name": {
      "include": ["refs/heads/task/**/*"],
      "exclude": []
    }
  },
  "rules": [
    { "type": "non_fast_forward" }
  ]
}
EOF

gh api -X POST repos/Arasz/ai-raccoon/rulesets --input ruleset.json
```

Then verify the `include` pattern actually reaches a task branch — this endpoint reports the rules
that apply to a named ref, so it catches a pattern that matches nothing:

```bash
gh api "repos/Arasz/ai-raccoon/rules/branches/task%2Fpd3-ref-rewrite-investigation" --jq '[.[] | .type]'
# expect the list to contain "non_fast_forward"
```

To retire it: `gh api -X DELETE repos/Arasz/ai-raccoon/rulesets/<id>`.

### 5.4 Proposed rule text — not applied

One line for `CLAUDE.md` and `.ai-badger/CLAUDE.md`. `.ai-badger/CLAUDE.md` is the source of truth and
the project copy is generated, so the owner edits the source and re-runs `welcome-ai-badger`:

> **Never rewrite a pushed branch.** Integrate with `git merge origin/main` — never `git rebase` a
> branch that has been pushed, never `git pull` (`pull.rebase` is true globally), and never any force
> variant of `git push`.

The same content as a standing lane brief, for a dispatch prompt:

> merge `origin/main`; never rebase a pushed branch; never `git pull` while `pull.rebase` is true.

## 6. Acceptance

- Every table row carries an evidence command run today, and both commits of every pair were read out
  of the object store.
- No claim is made about who issued the six unattributed pushes.
- Mitigation commands are copy-pasteable; the permission-pattern syntax is quoted from the Claude Code
  permissions reference read today, not guessed, and it is what proves the current entry dead.
