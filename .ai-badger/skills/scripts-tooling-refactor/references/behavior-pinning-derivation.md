# Behavior-pinning derivation for byte-identical script splits

Full recipe behind "Step 4b — derive, don't assume". Validated on the project
P5 (1133-line ingest-the reference repo-docs.py split into 6 src modules, hash contract
byte-identical across the real 198-file corpus).

## When

A work package requires behavior "byte-identical to today" (a hash map, a
golden file, C# tests depending on outputs). Expected values for the TDD tests
must come from EXECUTING the current code, never from reading it — reading
misses dead code and quirks; assumption silently "fixes" them and breaks the
contract.

## Derivation script skeleton

```python
import importlib.util, json, sys

spec = importlib.util.spec_from_file_location(
    "ingest_current", "scripts/ingest-the reference repo-docs.py")
m = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = m          # REQUIRED before exec_module
spec.loader.exec_module(m)          # else: AttributeError: 'NoneType' object
                                    # has no attribute '__dict__' (dataclass
                                    # fields + `from __future__ import annotations`)

out = {}
out["split_h2"] = m._split_by_h2("# Title\n\nintro\n\n## A\n\nbody")
out["chunk_adr"] = [(c.structured_path, c.content, c.context, c.source_file)
                    for c in m.chunk_file("docs/adr/x.md", FIXTURE, "adr", "docs:adr")]
out["exclude_quirk"] = m._matches_exclude(".remember/today-2026-07-20.md")
out["hash_golden"] = m.compute_expected_hash("## Framework\n\nUses React 18.")
# ... classify table, path helpers, hash-map dup handling ...
print(json.dumps(out, indent=1, sort_keys=True))
```

Then paste the dump into the test file VERBATIM (hardcoded golden values).
Do not hand-retype or "clean up" the expected tables — types drift (a
bare-string row vs a tuple row in an expected list fails the comparison for a
reason that has nothing to do with the code under test).

## What derivation actually catches (P5 finds)

- `.remember/today-*.md` exclusion was DEAD: `_matches_exclude` only has an
  exact-match branch for non-`/`-suffixed, non-`/*`-suffixed patterns, so
  `today-2026-07-20.md` returned False. Docstring promised exclusion; code
  didn't deliver. Pinned as-is (False), flagged in the report.
- `chunk_adr` preamble chunk content was `# Title\n\n## preamble\n\n# Title`
  (H1 duplicated inside the preamble body). Pinned verbatim.
- The "frontmatter-only fallback" branch of `chunk_file` is unreachable for
  non-empty text — didn't invent a test for it, pinned only reachable paths.

## Split-required adjustments checklist (the ONLY deliberate changes)

1. `__file__`-derived paths move with the module: `HASH_MAP_PATH` needed
   `.parent.parent` (src/ → scripts/). Assert the resolved path still equals
   the original target; the smoke run proves it (map landed at
   scripts/chunk-hash-map.json, not scripts/src/).
2. Test parameterization: add a defaulted parameter
   (`enumerate_files(root: Path = JSAA_ROOT)`); default behavior identical.
3. Dead code preserved verbatim (unused locals like `current_header`,
   unreachable branches) — the move is mechanical; cleanup is a separate
   change. Inline `import fnmatch` may move to module top (import location is
   not behavior).
4. Wrapper re-exports contract constants (`from the reference repo_config import PROJECT_ID`)
   so external comments that name the wrapper file stay truthful.
5. Per-module `log = logging.getLogger("ingest")` — same logger name, no
   shared mutable state; the wrapper's `logging.basicConfig` configures all.

## Golden-artifact smoke (hash-contract gate)

```bash
cp scripts/chunk-hash-map.json /tmp/hash.committed.json
python3 scripts/ingest-the reference repo-docs.py --chunk-only        # no-write mode
python3 - <<'EOF'
import json
c = json.load(open('/tmp/hash.committed.json'))
p = json.load(open('scripts/chunk-hash-map.json'))
print(len(c), len(p), open('/tmp/hash.committed.json').read() == open('scripts/chunk-hash-map.json').read())
EOF
```

Also diff key sets and per-key values, not just the JSON text. Byte-identical
full-corpus output is the strongest parity evidence: it exercises
enumerate + classify + chunk + hash over the real tree. If it mismatches:
investigate and report — don't auto-fail, don't silently rewrite the golden.
Restore the committed file if the smoke modified it.

## No-touch smoke (when regeneration is forbidden)

Some packages forbid regenerating tracked artifacts (P6 benchmark corpus:
"regeneration stays a deliberate owner act"). Prove byte-identity anyway by
redirecting the module-global output constant to tmp dirs and diffing
legacy-vs-new — no tracked file is ever written:

```python
# 1. LEGACY run — even after the working-tree file was rewritten:
text = subprocess.run(["git", "-C", WT, "show",
    "HEAD:scripts/generate-benchmark-corpus.py"], capture_output=True,
    check=True, text=True).stdout
ns = {"__name__": "smoke_legacy", "__file__": "smoke_legacy.py"}
exec(compile(text, "smoke_legacy.py", "exec"), ns)
ns["OUT"] = tmp1                    # patch the module global BEFORE calling;
                                    # functions read globals at call time
docs = ns["collect_docs"](); ns["emit_cs"](docs, ns["build_queries"](docs))

# 2. NEW wrapper run — importlib exec_module executes the wrapper's
#    sys.path.insert + imports exactly like a real invocation (faithful
#    subprocess-free smoke of main()):
import benchmark_corpus as bc      # the NEW src module
bc.OUT = tmp2                      # wrapper's `from benchmark_corpus import
                                   # emit_cs` binds the same function object,
                                   # which reads OUT from ITS module
spec = importlib.util.spec_from_file_location("gbc", "scripts/generate-benchmark-corpus.py")
wm = importlib.util.module_from_spec(spec); sys.modules[spec.name] = wm
spec.loader.exec_module(wm); wm.main()

# 3. diff -r tmp1 tmp2 → byte-identical is the parity proof. Then diff tmp2
#    against the committed artifacts for DRIFT REPORTING only: source repos
#    evolve, so committed != current is expected and NOT refactor-caused
#    when legacy == new. Report the drift delta, leave regeneration to the
#    owner.
```

Requires a clean-ish baseline: snapshot `git status --short` FIRST — HEAD is
only the pre-change script if the tree was clean at start (or if the change
is uncommitted, which also works: HEAD still holds the original).

## P6 finds (benchmark corpus)

- Substring keyword matching: topic labels match on plain `in` substring
  checks over lowercased title+body — "consistently" contains "ci", so an
  ADR about naming gets the ci-cost topic. Substring noise is expected; pin
  it, don't "fix" it.
- `strip_md` has NO fence branch: "```\ncode block\n```" → "code block" via
  the inline-code regex eating both backtick pairs (trace: pair 1 matches
  empty group, pair 2 captures the interior, pair 3 matches empty).
- `body_excerpt` truncation can cut mid-word at MAX_BODY_CHARS (appends
  sentences until >= cap, then slices).

## Expected-artifact transcription

Emitted C# contains `"""` (raw-literal delimiters) and backslashes, which
break raw triple-quoted Python literals. Store expected content as a list of
lines joined with `"\n"` (mirrors the emit code's own structure; escapes are
predictable, and the line list reads as the file structure).

## Version-safety gate

Run the pytest gate on BOTH the dev python and the oldest supported system
python (`/usr/bin/python3` = 3.9) — proves the "3.9-safe syntax" claim with
real interpreter evidence instead of reading the syntax.
