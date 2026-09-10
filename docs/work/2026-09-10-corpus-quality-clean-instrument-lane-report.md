# Lane report — `air-corpus-quality-clean-instrument` (implement lane)

Branch `task/air-corpus-quality-clean-instrument-lane-impl`, base `db2906e0`.
One commit per package (B1/B2/B3/B4); no push, no merge — the orchestrator merges
after review. Plan:
`.ai-badger/task-tracking/plans/2026-09-10-air-corpus-quality-clean-instrument.md`
(read at its absolute path, outside the worktree). Supporting records:
`docs/work/2026-09-10-corpus-quality-research.md` (measured rig facts),
`docs/work/2026-09-10-corpus-quality-plan-review.md` (independent review — its
findings were already folded into the plan; this lane re-litigates none of them,
and found none of them wrong in execution).

## Method

B0 reproduction → B1 generator+tests (TDD, RED first) → B2 regeneration →
B3 two-run campaign + report → B4 integration. Every `- [ ]` below names its gate
with the exact command and captured output (truncated logs keep summary lines,
CLEAN/diff lines, FAILs). Every new/changed check was run RED (output pasted)
before GREEN; every added gate was broken on purpose once. Heavy commands ran one
at a time from `scripts/retrieval_tuning` under
`python3 memwatch.py --cap-mb 12288 --log <log> -- …`, never port 7721,
`--offline`. Machine load was ~3–14 (quiet; the plan budgeted for 20–40), so
repeats ran 4–7 min, not 13–25.

## B0 — rig reproduction gate (artifact untouched) ✅

B0.1 preconditions (repo root), all exact:

```
shasum -a 256 /tmp/p1-live-copy.db
e0434a7214ac4caf1dbbef56147f582515bd0f5ebda665ad8cb06687296a55f6
sqlite3 /tmp/p1-live-copy.db 'SELECT count(*) FROM entries;'   → 56457
params.json → e0434a7214ac 56457 cb950dc80d67 869254400 24
project-corpus-100.json → 0a7ecb0133642817b30fa955c3fc7d09631a65d8a679e34173435e8c8043004e
eval-set-100.json       → 5cc5bcdf3bd405215ad5aef9d09b41a29c7770aa316c648de67ab960c1c482a8
docs/work/results-f1.json → f3e123c5a81a6b7dd5bc17bb27b013d7213ab1a746fe803f1b4873cd434dcf5b
```

B0.2 quiesced base rebuilt (`make_quiesced_scratch.py --source
/tmp/p1-live-copy.db --out /tmp/b0-frozen/base-quiet.db`):

```
rows: 56457 (copy parity verified)
settings disabled: 12 (watch/sweep/extract kill switches)
base sha256: eeb431a3efd8933811714d1fb3fa7e97bb26ae9e95f4573bcaa71d8588a5e41d
```

B0.3 frozen reproduction, base mode, `--repeats 1` (17:54:59→18:00:50 CEST,
~6 min):

```
memwatch: peak=5415MB cap=12288MB result=exit(0)
WARNING: 1 stale anchors (re-chunked upstream, unhittable by either leg): ['C035']
WARNING: repeat 1: scratch copy sha eeb431a3efd8... != store copy sha e0434a7214ac... (row count matches: access-bump mutation from a prior run)
eval: n=99 paired=99 stale=1 repeats=1 harness hit-rate=0.677 f1=0.158 | ai-raccoon hit-rate=0.808 f1=0.187 | mcc=0.5954897352959921 cont={'a': 65, 'b': 2, 'c': 15, 'd': 17}
python3 diff_golden.py ../../docs/work/results-f1.json /tmp/b0-frozen/results.json
CLEAN: no differences under the single tolerance term (exact hashes/hits/contingency/MCC, mean-F1 +/-1e-9)
```

Gate: CLEAN + summary + staleids line — all match the plan's expected values
(`cont=[65,2,15,17]`, `mcc=0.5955`). **B0 CLEAN — proceeded to B1.**

## B1 — generator change + tests ✅ (commit `3839b50b`)

B1.1 — new stdlib-only `scripts/src/retrieval_tuning/corpus_text.py`
(`repair_topic(value, *, fallback=None)`, `TOPIC_MAX_CHARS = 100`,
`TOPIC_DERIVATION = "markup-aware-v1"`), porting the measured prototype plus the
`@`-scoped-package strip. TDD RED first:

```
python3 -m pytest scripts/tests/test_corpus_text.py -q
E   ImportError: cannot import name 'corpus_text' from 'retrieval_tuning'
1 error during collection
```

GREEN after: `8 passed`. T1–T5 assert exact expected strings on fixtures
(derived empirically from a scratch port before freezing). Break-on-purpose
mutations (all RED pasted, then restored to GREEN `8 passed`):

- T4 (drop the `@` strip): `AssertionError: assert 'The @scope/p...ndles retries' == 'The scope/pk...ndles retries'`
  (`- The scope/pkg adapter handles retries` / `+ The @scope/pkg adapter handles retries`)
- T6 (add `import numpy`): `AssertionError: non-stdlib imports in corpus_text: ['numpy']`
- T7 (inline `DEBRIS_SIGNATURE` reference): `AssertionError: corpus_text must not reference DEBRIS_SIGNATURE: ...`
- T7 note: the first T7 RED was self-inflicted — the module docstring named the
  symbols and the guard failed on it. The guard worked as designed; the docstring
  was reworded to avoid the literals. An `import debris_query` mutation REDs via
  collection error (`ModuleNotFoundError: No module named 'llamaindex_harness'`
  in the source-tree context), so the inline-symbol run above is the recorded
  guard RED.

B1.2 — `build_project_corpus._derive_topic` delegates to
`corpus_text.repair_topic` (with `fallback` passthrough); `_render_query` passes
the first discriminator as the fallback; header gains `"topicDerivation"`;
removed the now-dead `_TOPIC_MAX_CHARS`/`_TOPIC_MIN_CUT` constants (no behavior
change). RED against the old artifact before B2 (with
`AI_RACCOON_EVAL_COPY=/tmp/p1-live-copy.db`):

```
test_committed_corpus_is_debris_free:
  AssertionError: regenerated corpus still carries debris in 23/100: ['C017', 'C021', 'C026', 'C028', 'C036', 'C044', 'C045', 'C048', 'C049', 'C050', 'C051', 'C052', 'C053', 'C056', 'C057', 'C059', 'C060', 'C072', 'C078', 'C096', 'C097', 'C099', 'C100']
  assert [...23 items...] == []
test_committed_artifact_records_the_topic_derivation:
  assert None == 'markup-aware-v1'
```

(plus T10 shape and the determinism committed-half RED on header/generation
drift — same cause, GREEN after B2.)

B1.3 — P3 parity project arm rewritten to the new contract ((a) new output ==
committed artifact; (b) legacy vs new identical except `query`/`frame`, header
except `topicDerivation`; (c) every legacy-debris query changed and clean;
anti-vacuity: legacy output must yield exactly the 23 pinned debris ids, (a)
checked first, repo-anchored `debris_query` import). Pre-B oracle baseline
(current test, pre-change generator): `1 passed in 16.58s` (legacy == new bytes).
Post-change, pre-B2: RED on (a) as designed —

```
E  AssertionError: (a) new project-corpus output differs from the committed artifact
E  assert '5445425c695a...ce0ba25d02ac3' == '0a7ecb013364...35e8c8043004e'
1 failed
```

— while (b)/(c) were verified GREEN on preview output (see B2.1). Full GREEN
after B2.1 (`1 passed in 16.65s`).

B1.4 — T6/T7 mutation transcripts pasted above (B1.1).

## B2 — regenerated corpus ✅ (commit `104c5842`)

B2.1 — regenerated in place from `/tmp/p1-live-copy.db` (old bytes backed to
`/tmp/b2-preview/old-committed.json` first). New artifact sha
`5445425c695a9ad1379f89b7f7e83e6816eeec469672ac75455ce0ba25d02ac3`;
`cmp` preview-vs-committed: identical (determinism holds across runs).

Preview gates on the committed bytes:

```
debris 0/100 []
header_diff: {'topicDerivation'} | topicDerivation: markup-aware-v1
non-text field diffs: NONE; query texts changed 85/100; frames shifted 2 ['C023', 'C040']
legacy_debris repaired: True
STRICT changed==legacy_debris: False (extra=62, missing=0)
```

**Relaxation (deliberate, with cause — the plan's own fallback path).** The
strict `set(changed) == set(legacy_debris)` gate assumed the repair only alters
debris rows; the measurement falsifies that assumption (the prototype never
checked clean-row churn). The repair selects the first clean sentence span
(split on `[.!?]`) while the legacy cut stopped at the first `[.;:!?]` — so 62
clean rows changed text: legacy `:`/`;` mid-sentence cuts now extend to the
sentence end, and markup-led first clauses (headings, bullets, bold,
backticks, `--- name`) are skipped for later clean spans. Collision fallout
accounts for only the 2 frame shifts (C023, C040); anchors, scopes, projects,
hashes, markers, holdout and allocation are byte-identical for all 100 ids, so
the repair-only / target-preserving claim stands. A narrower hybrid
("keep the legacy cut when clean") was rejected: it would need a cleanliness
oracle in the generator (T7 tension) and would make `topicDerivation` mean two
different derivations across rows; uniform application is the simpler shape and
the header claim stays true for every row. All 23 debris rows repaired, 0 new
debris — no T8 floor relaxation needed (debris is exact zero, stricter than the
AC's ≤5).

The 23 old→new topic texts (SHOULD-1 human review — the metric proves
markup-free, not good prose):

```
C017: OLD How is ], "intent handled?
C017: NEW How is intent Close the browser and end the browsing session. origin manual handled?
C021: OLD Where is ``` BenchmarkDotNet v0.15.8, macOS Tahoe 26.6.2 (25G83) [Darwin 25.6.0] Apple M4, 1 CPU, 10 logical documented?
C021: NEW Where is BenchmarkDotNet v0.15.8 macOS Tahoe 25G83 Darwin Apple M4 CPU logical and documented?
C026: OLD How is | lmstudio:text-embedding-qwen3-embedding-0.6b | 1024 | 0.326 | 0.378 | 0.854 | 0.606 | | handled?
C026: NEW How is lmstudio:text-embedding-qwen3-embedding-0.6b lmstudio:text-embedding-embeddinggemma-300m What each handled?
C028: OLD What do our notes say about > row deliberately did not?
C028: NEW What do our notes say about row deliberately did not?
C036: OLD What do our notes say about - [Session 2026-07-26 Final?
C036: NEW What do our notes say about Session 2026-07-26 Final: All Systems Healthy — 477 tests ✓ (362 FE + 115 BE), build successful?
C044: OLD How is Running benchmarks Follow [Get started with the Python SDK](docs/user/guide/python-sdk.md) to handled?
C044: NEW How is Running benchmarks Follow Get started with the Python SDK to install the SDK and run the handled?
C045: OLD Where is ``` workflow/ workflow capability + worker-thread provider + tool Consumer todo/ todo_write tool documented?
C045: NEW Where is workflow workflow capability worker-thread provider tool Consumer todo todo write tool documented?
C048: OLD Where is DeepSeek Harness English | [中文](README.zh.md) DeepSeek Harness (`dsh`) is an open-source agent documented?
C048: NEW Where is It uses an architecture where everything is a plugin, and is powered by Cordis, whose design is documented?
C049: OLD What do our notes say about - 为你的插件仓库添加 [`dsh-plugin`](https://github.com/topics/dsh-plugin) 话题，便于被发现。 - 欢迎加入 DeepSeek Harness 企?
C049: NEW What do our notes say about 为你的插件仓库添加 dsh-plugin 话题，便于被发现。 欢迎加入 DeepSeek Harness 企微群：扫码添加企微小助手并填写入群问卷，完成后小助手会邀请你入群。 企微小助手 入群问卷?
C050: OLD How is | [`@anthropic-ai/claude-agent-sdk-darwin-x64`](https://www.npmjs.com/package/@anthropic-ai/claude-a handled?
C050: NEW How is anthropic-ai/claude-agent-sdk-darwin-x64 anthropic-ai/claude-agent-sdk-linux-arm64 handled?
C051: OLD Where is `@deepseek-ai/dsh` English | [中文](README.zh.md) The `dsh` command is the product launcher for documented?
C051: NEW Where is src/args.ts owns the command grammar, and src/bin.ts loads only the selected runner documented?
C052: OLD What do our notes say about | `dsh --profile headless "job"` | 运行一个全新的持久化会话，打印最终答案并退出。 | | `dsh web` | `--profile web` 的别名。 | |?
C052: NEW What do our notes say about dsh --profile headless "job" --profile web 的别名。 dsh plugin --profile 运行命令时所在的目录将作为默认 workspace?
C053: OLD How is ```mermaid plugin_dsh_base_tool_result_pruner["tool-result-pruner<br/>@deepseek-ai/dsh-compaction-to handled?
C053: NEW How is plugindshbasetoolresult pruner tool-result-prunerdeepseek-ai dsh-compaction-tool-result-pruner cfg handled?
C056: OLD How is "@deepseek-ai/dsh-settings handled?
C056: NEW How is deepseek-ai dsh-settings workspace deepseek-ai dsh-subagent workspace deepseek-ai dsh-system-prompt handled?
C057: OLD Where is Install external plugin bundles through `dsh plugin --profile <name> add <package-or-git-spec> documented?
C057: NEW Where is Install external plugin bundles through dsh plugin --profile add  documented?
C059: OLD How is { "references handled?
C059: NEW How is references path vendor cordis path vendor loader path vendor include path handled?
C060: OLD Where is { "name documented?
C060: NEW Where is name deepseek-ai dsh-web-frontend description Web application entry vite build over the documented?
C072: OLD What do our notes say about { "sdk": { "version": "9.0.0", "rollForward": "latestMinor", "allowPrerelease": false } }?
C072: NEW What do our notes say about sdk version rollForward latestMinor allowPrerelease?
C078: OLD Where is Memory Index ## jsaa — production & pipeline - [Full project review, all waves documented?
C078: NEW Where is Memory Index jsaa production pipeline Full project review all waves 2026-08-07 documented?
C096: OLD How is | Unit | Vitest 4 + @vue/test-utils (jsdom), v8 coverage | | E2E | Playwright (chromium) + axe-core handled?
C096: NEW How is Vitest 4 + vue/test-utils (jsdom), v8 coverage Playwright (chromium) + axe-core WCAG 2.1 AA scans handled?
C097: OLD Where is ":{"line":27,"column":20}},"loc":{"start":{"line":27,"column":30},"end":{"line":29,"column":1}},"lin documented?
C097: NEW Where is line column loc start line column end line column line name documented?
C099: OLD How is { "extends handled?
C099: NEW How is extends tsconfig node24 tsconfig.json include compilerOptions module preserve moduleResolution handled?
...[truncated 17950 chars]