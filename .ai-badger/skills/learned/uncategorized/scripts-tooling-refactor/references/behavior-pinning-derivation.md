# Behavior-pinning derivation for byte-identical script splits

Full recipe behind "Step 4b — derive, don't assume". Validated on ai-raccoon
P5 (1133-line ingest-jsaa-docs.py split into 6 src modules, hash contract
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
    "ingest_current", "scripts/ingest-jsaa-docs.py")
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
4. Wrapper re-exports contract constants (`from jsaa_config import PROJECT_ID`)
   so external comments that name the wrapper file stay truthful.
5. Per-module `log = logging.getLogger("ingest")` — same logger name, no
   shared mutable state; the wrapper's `logging.basicConfig` configures all.

## Golden-artifact smoke (hash-contract gate)

```bash
cp scripts/chunk-hash-map.json /tmp/hash.committed.json
python3 scripts/ingest-jsaa-docs.py --chunk-only        # no-write mode
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

## Version-safety gate

Run the pytest gate on BOTH the dev python and the oldest supported system
python (`/usr/bin/python3` = 3.9) — proves the "3.9-safe syntax" claim with
real interpreter evidence instead of reading the syntax.
