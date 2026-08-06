# Plan: scripts/ directory refactor — src/ + tests/, all scripts python, dead scripts removed

Status: proposed · Branch: task/scripts-refactor · Persona: architect (planning only — no code)

Owner spec (verbatim): "1. src and tests dir in scripts 2. all logic to src 3. TDD (we add tests first) 4. start" plus "all non python scripts converted to python" and "all old / not used scripts - removed".

Scope: 11 tracked files in scripts/ (3 shell, 5 python, 1 html, 2 json data) + 1 gitignored output. ~2000-line mechanical refactor. No new third-party dependencies.

---

## 1. Inventory decisions (per file, with evidence)

| File | Decision | Evidence |
|---|---|---|
| `download-embedding-model.sh` (76 ln) | **CONVERT** → `download-embedding-model.py` | Run in CI: `.github/workflows/publish.yml:50` `bash scripts/download-embedding-model.sh`; live refs: `benchmarks/README.md:46`, `src/AiRaccoon/AiRaccoon.csproj:39` (comment), `tests/.../Unit/Embedding/BundledModelGateTests.cs:22` (error msg), `benchmarks/.../Embedders/LocalGgufEmbedder.cs:32` (error msg), `tests/.../Unit/Retrieval/README.md:14`, `docs/reference/agent-memory-server.md:375`, `docs/reference/embedding-benchmark.md:98,107`, `.gitignore:19` (comment), `scripts/manual-fresh-install-test.py:47` (sync contract) |
| `verify-tool-package.sh` (49 ln) | **CONVERT** → `verify-tool-package.py` | Local pre-publish pack gate (not in CI); live refs: `publish.yml:11` (comment), `manual-fresh-install-test.py:5,47-49`; historical: `docs/reviews/2026-08-06-integration-review-1-0-8.md:154`, `docs/work/2026-08-06-nuget-package-id-migration.md:66` |
| `regenerate-retrieval-golden.sh` (16 ln) | **CONVERT** → `regenerate-retrieval-golden.py` | Live refs: `tests/.../Unit/Retrieval/GoldenFileTests.cs:15,37`, `tests/.../Unit/Retrieval/README.md:43`; own line 5 mentions `generate-benchmark-corpus.py` (unchanged) |
| `patch-tool-shell.py` (125 ln) | **MOVE-TO-SRC** (wrapper stays at `scripts/patch-tool-shell.py`) | Run in CI: `publish.yml:68` `python3 scripts/patch-tool-shell.py …` — path must stay stable |
| `manual-fresh-install-test.py` (271 ln) | **MOVE-TO-SRC** (pure helpers only; orchestration stays in wrapper) | Owner's post-publish finish gate; live refs: `docs/work/2026-08-06-nuget-package-id-migration.md:66,81` (historical), `docs/reviews/*:93,147,166` (historical) |
| `generate-benchmark-corpus.py` (283 ln) | **MOVE-TO-SRC** | Live refs: `benchmarks/README.md:14`, `benchmarks/.../Corpus/RealWorldCorpus.cs:2`, `BenchmarkCorpus.cs:6` (comments), `tests/.../Unit/Retrieval/README.md:53`, `docs/reference/embedding-benchmark.md:117` |
| `ingest-jsaa-docs.py` (1133 ln) | **MOVE-TO-SRC** (split into 6 modules) | Contract: `scripts/chunk-hash-map.json` read by 7 C# integration test files (see §4); `PROJECT_ID` comment referenced by 6 test files; ADR-0003/0004 |
| `run-baseline-queries.py` (213 ln) | **REMOVE** | **Verified: no live consumers.** Only references are the script itself, `docs/plans/doc-ingestion-implementation-plan.md` (original plan, superseded), `docs/plans/retrieval-improvement-b.md`, `docs/plans/memory-audit-fixes.md:63` (all historical plans — not CI, tests, benchmarks, or current reference docs). Output `baseline-results.json` is gitignored and its only consumer is `scoring-form.html` (also removed). The current baseline gates are the C# `BaselineMetricsTests`/`RetrievalBaselineTests` suites (docs/work/2026-08-06-baseline-repin-new-corpus.md). Caveat: `docs/plans/memory-audit-fixes.md:63` (an ops plan, left untouched) instructs re-running it — the script stays recoverable from git history if the owner ever needs the live-server probe again |
| `scoring-form.html` | **REMOVE** | **Verified: no live consumers.** Referenced only by `docs/plans/doc-ingestion-implementation-plan.md` Section C (manual scoring workflow, superseded by automated C# metrics per BaselineMetricsTests). No CI/test/docs-reference hits |
| `chunk-hash-map.json` | **KEEP** at scripts/ root | C# tests hardcode `scripts/chunk-hash-map.json`: RetrievalBaselineTests:392, SourceIdentityTests:213, SourceAffinitySweepTests:351, RrfParameterSweepTests:545, BaselineMetricsTests:413, QueryConstructionTests:295, SectionTargetedRetrievalTests:345 |
| `baseline-queries.json` | **KEEP** at scripts/ root | BaselineQueryCatalogTests:137 + the six files above |
| `baseline-results.json` (gitignored) | **LEAVE** (untracked, orphaned) | `.gitignore:24-25` entries removed in P9; file itself untouched |

**Corrections to the pre-gathered evidence (verified in the worktree):**
- `ingest-jsaa-docs.py` and `run-baseline-queries.py` import **httpx** (third-party) — the "stdlib-only" claim is false for these two. httpx is an existing ambient dependency, not a new one; tests must not require it.
- Additional live `.sh` references beyond the task's checklist: `BundledModelGateTests.cs:22`, `LocalGgufEmbedder.cs:32`, `publish.yml:11`, `.gitignore:19`, `docs/reference/agent-memory-server.md:375`, `docs/reference/embedding-benchmark.md:98,107` (all updated in P9).
- Host pythons: `/usr/bin/python3` = 3.9.6 (system, pytest 8.4.2 works) and `/opt/homebrew/bin/python3` = 3.14.6. All scripts use 3.9-safe syntax (no PEP 604 unions, no match).

---

## 2. Target layout

```
scripts/
  chunk-hash-map.json          # KEEP (C# contract)
  baseline-queries.json        # KEEP (C# contract)
  <name>.py                    # thin entrypoint wrappers (argparse/env + call into src)
  src/
    bundle.py                  # model-bundle contract: filenames + SHA-256 pins (ONNX, vocab, GGUF)
    download.py                # sha256_file(), fetch_verified() — urllib download + verify + .part + delete-on-mismatch
    package_verify.py          # parse_csproj_version(), detect_rid(), nupkg entry checks, sha of extracted entry
    tool_shell.py              # find_settings/parse_settings/build_settings/patch (DotnetToolSettings.xml rewrite)
    jsaa_config.py             # JSAA_ROOT, JSAA_PINNED_COMMIT, MCP_BASE, PROJECT_ID, BATCH_SIZE, HASH_MAP_PATH, CONTEXTS_TO_DELETE, SPOT_CHECKS
    chunking.py                # Chunk dataclass, text helpers, chunk_adr/heading/atomic/skill/remember/rules, CHUNKER_MAP, chunk_file
    sources.py                 # _matches_exclude, classify_file, enumerate_files
    hash_map.py                # compute_expected_hash, chunk_written_content, build_hash_map   (the chunk-hash-map.json contract)
    mcp_client.py              # AiRaccoonClient, _unwrap (httpx adapter)
    pipeline.py                # write_chunks_batched, run_spot_checks, reset_contexts, verify_jsaa_pin, run_pipeline
    benchmark_corpus.py        # strip_md, first_heading, body_excerpt, safe_id, csharp_literal, csharp_string, collect_docs, build_queries, emit_cs
    fresh_install.py           # sha(), unwrap() (pure helpers from manual-fresh-install-test.py)
  tests/
    test_bundle.py  test_download.py  test_package_verify.py  test_tool_shell.py
    test_chunking.py  test_sources.py  test_hash_map.py
    test_benchmark_corpus.py  test_fresh_install.py
pyproject.toml                 # NEW at repo root (see below)
```

**Import pattern** (every wrapper, stdlib-only, 3 lines):
```python
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))
from bundle import MODEL_SHA256        # flat modules, no package ceremony
```
Alternative considered (namespace-package `from src import …` relying on `sys.path[0]` = scripts/): rejected — fragile under different invocation styles. The insert pattern works for every current caller (`python3 scripts/<name>.py …`, incl. publish.yml:68).

**pyproject.toml** (new, config-only, zero dependencies — satisfies `python.instructions.md`'s interpreter declaration without inventing a package):
```toml
[project]
name = "ai-raccoon-scripts"
requires-python = ">=3.9"

[tool.pytest.ini_options]
testpaths = ["scripts/tests"]
pythonpath = ["scripts/src"]
```
`pythonpath` (pytest ≥7) puts src/ on sys.path for tests — no conftest needed. This is the single test-runner convention: **`python3 -m pytest scripts/tests`** from the repo root. Python tests are NOT wired into `dotnet test`.

**Why wrappers stay:** every external caller references `scripts/<name>` paths — CI (publish.yml:50,68), benchmarks/README, tests READMEs, C# error messages, and the owner's gate ritual. Only the 3 `.sh` renames change references (P9). The C# tests' `PROJECT_ID` comments ("matches … scripts/ingest-jsaa-docs.py") stay truthful because the wrapper file keeps its name and re-exports `PROJECT_ID`.

---

## 3. TDD test plan (tests first, per module; hermetic — no network, no live server, no dotnet)

All tests: pytest, tmp_path fixtures, inline strings only. Red by absence of the src module, green after the logic is moved.

| Module | Test cases |
|---|---|
| `bundle` | Pins match the current .sh constants exactly (model `4278337f…`, vocab `07eced37…`, gguf `908c82ac…`; filenames `model_qint8_arm64.onnx`, `vocab.txt`, `all-MiniLM-L6-v2.Q5_K_M.gguf`) — this test is the three-way sync contract, enforced once instead of thrice |
| `download` | `sha256_file` matches `hashlib` reference on a tmp file; `fetch_verified` skips download when file exists+matches (pre-seeded tmp file, no network); downloads + verifies via `file://` URL; mismatch → file deleted + error raised; `.part` cleanup on failure |
| `package_verify` | `parse_csproj_version` on a fixture csproj string (found / missing); `detect_rid` mapping table (darwin-arm64→osx-arm64, linux-x86_64→linux-x64, etc.); nupkg entry presence checks on in-memory zips (with/without `Models/…` entries); sha256 of an extracted zip entry vs `bundle.MODEL_SHA256` |
| `tool_shell` | `parse_settings` on the SDK-emitted 1-RID shape (with and without EntryPoint/Runner) and on unexpected shapes (0 or 2 RID entries → SystemExit); `build_settings` round-trip `parse(build(x)) == x` for all 6 RIDs; end-to-end in-memory zip patch: settings + `[Content_Types].xml` + one payload entry → patch → every RID present exactly once, content-types byte-identical, entry set unchanged; duplicate-rid rejection; ≠6 rids rejection |
| `chunking` | Pin CURRENT behavior with representative inline fixtures (an ADR, a README with `##` sections, a SKILL.md, a remember note, a rules file): `_split_by_h2` splits on `##` keeping title; `_extract_title`; `chunk_adr` produces expected structured_path + content (decision/footer handling); `chunk_heading` per-section chunks; `chunk_atomic` single chunk; `chunk_skill`, `chunk_remember`, `chunk_rules` shapes; `chunk_file` dispatch via CHUNKER_MAP; `_chunk_path`/`_short_rel`/`_source_prefix` path building |
| `sources` | `classify_file` per extension/pattern (docs/adr, docs/plans, README, skills, remember notes, plus every exclusion rule in `_matches_exclude`); `enumerate_files` over a tmp_path fake repo tree (root parameterized) — asserts relative paths, type keys, exclusions applied |
| `hash_map` | `compute_expected_hash` determinism (same content → same hash; different content → different hash) — **byte-stability is the C# contract**; `chunk_written_content` reconstructs the exact written payload; `build_hash_map` shape (structured path → hash) + duplicate-path handling |
| `benchmark_corpus` | `strip_md` (headings, links, fences); `first_heading`; `body_excerpt` truncation at MAX_BODY_CHARS; `safe_id`; `csharp_literal`/`csharp_string` escaping (quotes, backslashes); `build_queries` deterministic count/shape from fake docs; `emit_cs` writes an expected C# skeleton (tmp_path); `collect_docs` over tmp fake repos |
| `fresh_install` | `sha()` equals hashlib reference on a tmp file; `unwrap()` on MCP result shapes (text-content JSON, error shape, passthrough) |

**Explicitly NOT unit-testable (orchestration/adapters) — smoke coverage instead:**
- `pipeline.py` (write/embed/spot-checks/reset/pin) — network + git; smoke = `python3 scripts/ingest-jsaa-docs.py --chunk-only` (local repo, no writes).
- `mcp_client.py` — thin JSON-RPC adapter; optionally testable with `httpx.MockTransport` (httpx already a dep) — mark optional, not required.
- `manual-fresh-install-test.py` steps 0–14 (dotnet tool install, MCP stdio round-trip) — owner's post-publish ritual; unchanged.
- `verify-tool-package.py` `dotnet pack` orchestration — owner's pre-publish gate; unchanged.
- `regenerate-retrieval-golden.py` — pure delegation (env var + dotnet test filter); no extractable logic, so no unit test by design (an abstraction with no buyer would violate ask-if-simpler). Gate = runs the C# golden test in regenerate mode.
- `download-embedding-model.py` full download — real 23 MB fetch; proven by CI on publish. Wrapper arg-error paths are smoke-checked.

---

## 4. Reference-update checklist (exact files; P9)

For the 3 `.sh` → `.py` renames (`download-embedding-model`, `verify-tool-package`, `regenerate-retrieval-golden`):

1. `.github/workflows/publish.yml:11` (comment: `verify-tool-package.sh`), `:49-50` (comment + `run: bash scripts/download-embedding-model.sh` → `python3 scripts/download-embedding-model.py`)
2. `.gitignore:19` (comment → `.py`); `:24-25` remove the now-dead `baseline-results.json` entries
3. `benchmarks/README.md:46` (`download-embedding-model.sh all-minilm` → `.py`)
4. `benchmarks/AiRaccoon.Benchmarks/Embedders/LocalGgufEmbedder.cs:32` (error message — not asserted anywhere; safe)
5. `docs/reference/agent-memory-server.md:375`; `docs/reference/embedding-benchmark.md:98,107` (current reference docs — update)
6. `src/AiRaccoon/AiRaccoon.csproj:39` (comment)
7. `tests/AiRaccoon.Tests/Unit/Embedding/BundledModelGateTests.cs:22` (ShouldBe message — not asserted; safe)
8. `tests/AiRaccoon.Tests/Unit/Retrieval/GoldenFileTests.cs:15,37` (comment + message)
9. `tests/AiRaccoon.Tests/Unit/Retrieval/README.md:14` (`.sh` → `.py`), `:43` (regenerate script → `.py`)
10. `scripts/manual-fresh-install-test.py:5,47-49` (docstring + sync contract) — updated inside P7, not P9

**Left untouched (historical by instruction):** `docs/plans/*`, `docs/reviews/*`, `docs/work/*` (incl. `features-native-memory/spec.json:79`), `docs/design/*`, `docs/research/*`, `docs/notes/*`, `baseline-retrieval-report.md`, `.ai-badger/state.json`. Flag to owner: `.ai-badger/skills/learned/**` (dotnet-tool-publishing references) also name the `.sh` scripts but are regenerated artifacts — update only if the owner wants them edited in place.

---

## 5. Ordered work packages (TDD: failing tests → src module → wrapper → gate)

**P0 — Scaffold.** New: `pyproject.toml` (root), `scripts/tests/` (dir). No logic.
AC: `python3 -m pytest scripts/tests` collects 0 tests, exit 0.
Gate: `python3 -m pytest scripts/tests`

**P1 — bundle contract + download conversion.** New: `src/bundle.py`, `src/download.py`, `tests/test_bundle.py`, `tests/test_download.py`, `scripts/download-embedding-model.py`; delete `scripts/download-embedding-model.sh`.
AC: pins byte-identical to today's .sh; urllib replaces curl; skip-if-verified and delete-on-mismatch semantics preserved; usage/arg-error paths preserved (`bogus` model → exit 2).
Gate: `python3 -m pytest scripts/tests/test_bundle.py scripts/tests/test_download.py`; smoke: `python3 scripts/download-embedding-model.py bogus` exits 2. (Real download is CI's proof.)

**P2 — verify-tool-package conversion.** Depends on P1 (imports `bundle`). New: `src/package_verify.py`, `tests/test_package_verify.py`, `scripts/verify-tool-package.py`; delete `scripts/verify-tool-package.sh`.
AC: version parse, RID detection (dotnet --info then uname fallback — both ported), zipfile-based entry listing (unzip race workaround becomes moot), sha256 of extracted entry vs bundle pin; pack orchestration via subprocess identical.
Gate: `python3 -m pytest scripts/tests/test_package_verify.py`; smoke: `python3 scripts/verify-tool-package.py` usage path. (Full pack gate = owner pre-publish ritual, unchanged.)

**P3 — regenerate-retrieval-golden conversion.** New: `scripts/regenerate-retrieval-golden.py`; delete `.sh`. No src module, no unit tests (rationale §3).
AC: sets `AIRACCOON_HARNESS_REGENERATE_GOLDEN=1`, runs the same dotnet test filter, same message.
Gate: `python3 -m py_compile scripts/regenerate-retrieval-golden.py`; owner-run regeneration when the golden file must move.

**P4 — patch-tool-shell refactor.** New: `src/tool_shell.py`, `tests/test_tool_shell.py`; rewrite `scripts/patch-tool-shell.py` to import from src (CLI + self-gate stay in wrapper).
AC: identical output shape; all self-gate checks preserved (6 rids, dup rejection, content-types unchanged, entry set unchanged); CI path `publish.yml:68` untouched.
Gate: `python3 -m pytest scripts/tests/test_tool_shell.py`; smoke: `python3 scripts/patch-tool-shell.py` (no args → usage exit).

**P5 — ingest-jsaa-docs refactor (largest).** New: `src/jsaa_config.py`, `src/chunking.py`, `src/sources.py`, `src/hash_map.py`, `src/mcp_client.py`, `src/pipeline.py`, `tests/test_chunking.py`, `tests/test_sources.py`, `tests/test_hash_map.py`; rewrite `scripts/ingest-jsaa-docs.py` as a thin CLI (argparse + config import + `pipeline.run_pipeline`).
AC: all five CLI modes keep their flags and exit behavior; **hash contract byte-stable** — `build_hash_map` output for the pinned-commit corpus must equal the committed `scripts/chunk-hash-map.json` (the C# tests depend on it); httpx stays the only third-party import (unchanged from today).
Gate: `python3 -m pytest scripts/tests/test_chunking.py scripts/tests/test_sources.py scripts/tests/test_hash_map.py`; smoke: `python3 scripts/ingest-jsaa-docs.py --chunk-only` (jsaa checkout at `JSAA_PINNED_COMMIT`; compares produced map vs committed `chunk-hash-map.json` — mismatch = investigate, not auto-fail).

**P6 — benchmark corpus refactor.** New: `src/benchmark_corpus.py`, `tests/test_benchmark_corpus.py`; rewrite `scripts/generate-benchmark-corpus.py` as thin CLI.
AC: emitted C# byte-identical to today for the same inputs; hardcoded `OUT`/repo paths preserved (behavior unchanged).
Gate: `python3 -m pytest scripts/tests/test_benchmark_corpus.py`. No regeneration smoke — it rewrites tracked corpus files; that stays a deliberate owner act.

**P7 — fresh-install helper extraction.** Depends on P1. New: `src/fresh_install.py`, `tests/test_fresh_install.py`; rewrite `scripts/manual-fresh-install-test.py` to import helpers + `bundle` pins (docstring at :5,47-49 updated here); the three-way manual sync warning dies — bundle.py is now the single source.
AC: script behavior identical (steps 0-14, env vars `AI_RACCOON_VERSION`/`AI_RACCOON_SOURCE`, exit codes); sha checks read `bundle` constants.
Gate: `python3 -m pytest scripts/tests/test_fresh_install.py`; full run = owner's post-publish ritual.

**P8 — removals.** `git rm scripts/run-baseline-queries.py scripts/scoring-form.html`. (`.gitignore` edits live in P9 to avoid a shared-file conflict.)
AC: both files gone; no live consumer remains.
Gate: repo-wide grep for `run-baseline-queries|scoring-form` matches ONLY historical docs (`docs/plans/*`, `docs/design/*`) and git history.

**P9 — reference updates.** All files in §4 (text-only).
AC: no live `.sh` references remain; `dotnet build` green; message-string changes don't affect tests (verified: none assert on them).
Gates: `grep -rn 'scripts/.*\.sh'` over live dirs (excluding `docs/plans|reviews|work|design|research|notes`, `.ai-badger/`, `baseline-retrieval-report.md`) → zero hits; `dotnet build`.

---

## 6. Parallelism and file-conflict map (ONE worktree — overlapping file sets are conflicts)

Shared files: `pyproject.toml` (P0 only), `scripts/tests/` (dir created in P0; each package owns a distinct test file), `scripts/src/` (each package owns distinct modules), `.gitignore` (P9 only), `scripts/manual-fresh-install-test.py` (P7 only).

```
P0 ──┬─► P1 (bundle+download) ──┬─► P2 (package_verify)
     │                          └─► P7 (fresh_install)
     ├─► P3 (golden wrapper)        [P2, P7 need bundle.py → after P1]
     ├─► P4 (tool_shell)
     ├─► P5 (ingest split)
     ├─► P6 (benchmark corpus)
     └─► P8 (removals)
P9 (reference updates) — last, after P1–P3 land (text must match the renames)
```

- **Serial chain:** P0 → P1 → {P2, P7}. P1 is the CI-critical path and the shared-contract package.
- **Parallel-safe after P0:** P3, P4, P5, P6, P8 — fully disjoint file sets (each writes only its own src/ module, test file, and wrapper).
- **P9 last** — text-only, touches files no other package owns.

---

## 7. Out of scope (explicitly)

- Wiring pytest into CI (`.github/workflows`) — owner decision, follow-up.
- Ruff / type-checker setup (`python.instructions.md` suggests it; the project has no `lint` command in `.ai-badger/config.json`) — follow-up, not part of this refactor.
- Touching historical docs: `docs/plans/*`, `docs/reviews/*`, `docs/work/*`, `docs/design/*`, `docs/research/*`, `docs/notes/*`, `baseline-retrieval-report.md`, `.ai-badger/state.json`.
- Moving `scripts/chunk-hash-map.json` / `scripts/baseline-queries.json` (C# tests hardcode `scripts/` paths).
- Behavior changes, new CLI flags, or new features — mechanical move only. The single deliberate improvement: the three-way manual constant sync (manual-fresh-install-test.py:46-50) replaced by importing `src/bundle.py`.
- New third-party dependencies (httpx stays as-is for the two ingest-related scripts; urllib replaces the curl subprocess; tests use pytest only).
- The orphaned gitignored `baseline-results.json` (untracked; the .gitignore entries die in P9).

## 8. Risks

- **Hash-contract stability (highest):** `compute_expected_hash`/`chunk_written_content` must produce byte-identical hashes — the committed `chunk-hash-map.json` and 7 C# test files depend on it. Mitigation: hash_map tests + P5 gate comparing regenerated map vs committed file.
- **Ambient httpx:** the ingest wrapper needs httpx in the interpreter's environment (no packaging, same as today). CI is unaffected (publish.yml never runs it).
- **Hardcoded absolute paths** (`JSAA_ROOT`, benchmark `OUT`, `LOCAL_SOURCE_DIR`): preserved verbatim; tests parameterize around them.
- **RID-detection fallback chain** in verify-tool-package: both paths (dotnet --info, uname mapping) ported 1:1.
- **P9 grep gate** must explicitly exclude historical dirs, or it fails on intent.
