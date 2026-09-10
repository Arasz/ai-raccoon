# P3 merge map — retrieval-harness dedup (AC1), reviewed against the 7-spelling inventory

Base: `12a72dfb` (task branch: P1 close-out + round 2 + P2 + C14 corrected).
New home: `scripts/src/retrieval_tuning/scopes.py` — the ONE scope/bucket builder.
`scripts/retrieval_tuning/llamaindex_harness/scopes.py` is an import-only shim
(bootstrap + re-export); `scripts/tests/test_retrieval_tuning_scopes.py` gates it
with `is`-identity assertions, so a re-copy that passes `==` still fails.

## The 7 spellings

| # | old spelling (file:line at `12a72dfb`) | encodes | new home | rewired call site |
|---|---|---|---|---|
| 1 | `ingest.load_rows` WHERE — `ingest.py:139-140` | ingest-rule set membership: resolved project buckets + global shared | `scopes.ingest_predicate` | `ingest.load_rows` |
| 2 | `ingest.verify_store` id-set WHERE — `ingest.py:514-515` | the same rule, verified rather than ingested | `scopes.ingest_predicate` | `ingest.verify_store` |
| 3 | `ingest._scope_predicate` — `ingest.py:395-404` | per-query predicate project / shared / all | `scopes.scope_predicate` | `ingest.query_fts` |
| 4 | probe legs — `ingest.py:555-558` | per-bucket committed legs + the shared leg, `e.`-aliased | `scopes.scope_predicate(..., prefix="e.")` | `ingest.fts_parity_probe` |
| 5 | `retrieve._chroma_where` — `retrieve.py:58-69` | Chroma `where` twin of #3 | `scopes.chroma_where` | `retrieve.FusionRetriever.vector_leg` |
| 6 | `slice_copy` keep-rule — `slice_copy.py:87-96` | shared tier whole + first `cap` committed rows per bucket | `scopes.slice_keep_clauses` | `slice_copy.slice_copy` |
| 7 | `corpus.VALID_SCOPES` / `corpus.load_corpus` — `corpus.py:17,28-35` (F6) | a second scope table whose validator rejected `custom`, and a second loader | `scopes.CORPUS_SCOPES` (derived from the one table) + `scopes.load_corpus` | `corpus.validate_entries`, `corpus.load_corpus` (delegates) |

Count check: **7 rows**, one per named spelling, plus the relocation and the
composition below — not a 3-count map.

### P1 relocation (no redefinition)

| moved | from | to |
|---|---|---|
| `VALID_SCOPES`, `SCOPE_FALLBACK`, `normalize_scope`, `load_corpus`, `resolve_buckets`, `_copy_projects` | `llamaindex_harness/scopes.py` | `retrieval_tuning/scopes.py`, verbatim |

Identity gate: `test_harness_shim_reexports_the_same_objects` asserts
`harness_scopes.resolve_buckets is scopes.resolve_buckets` (and the table, the
loader, the emitters), so the shim can never become a second definition.

### F6 decision (corpus.py validator)

`corpus.VALID_SCOPES` is now the *derived* corpus view
`CORPUS_SCOPES = frozenset(VALID_SCOPES) | frozenset(SCOPE_FALLBACK)` — the bank
table plus the corpus-only `custom` key. Deliberate behaviour change: `custom`
is a real corpus scope (`project-corpus-100.json` carries it) and the old table
rejected it; `galaxy` (and any other unknown) is still rejected. Not on the
full-100 eval path (`project-corpus-100.json` is consumed by the harness
loader, not `corpus.validate_entries`), so no eval behaviour moved.

### The other "one"s

| one thing | home | callers |
|---|---|---|
| one corpus loader | `scopes.load_corpus` (header-or-None, entries) | harness ingest/evaluate/slice, `corpus.load_corpus` |
| one default-scope table | `scopes.DEFAULT_QUERY_SCOPE` / `DEFAULT_SERVER_SCOPE` | harness eval/retrieve (`project`); `ScratchServer.search` (`all`) |
| one scratch composition | `retrieval_tuning/scratch.py` — `fresh_scratch_copy` + `scratch_server()` (fresh copy → pre-server checks → started server) | `evaluate` single-run and repeat paths |
| one anchor-resolution lib | `retrieval_tuning/corpus_anchors.py` (read-only open, sha256, hash uniqueness, marker matches) | both corpus generators + `refresh_corpora.verify_anchors` |

## Deliberately kept duplication (with reasons)

| kept | where | reason |
|---|---|---|
| Python scope membership for the exclusion manifest | `build_project_corpus._embedded_scope_counts` (`scope in ("project","custom")`) | It classifies *rows for a manifest count*, not a query or ingest predicate; routing it through the SQL emitters would invent a Python-scope table beside the SQL one. The exclusion prose (`build_project_corpus.py:182-184`) still names the rule. |
| bespoke probe server spawn | `src/retrieval_tuning/probe_sextant.start_server` | Old probe with a different contract (`--idle-timeout 0`, raw `Popen` + process-group kill for its own MCP handshake); not on the eval path. Merging it would change its protocol, not remove a spelling. |
| `memwatch.DEFAULT_CAP_MB` | `scripts/retrieval_tuning/memwatch.py` | P2's guard parameter, pinned by its own gate (`test_memwatch_default_cap_is_12gb`), not a retrieval knob. |
| two corpus-generator modules | `build_project_corpus.py`, `build_eval_corpus.py` | Different contracts (seeded allocation + marker uniqueness vs ADR allowlist + section families). Only the anchor primitives are shared (`corpus_anchors.py`). |
| threshold-eval runner | `run_threshold_eval.py` | Its own newline-JSON-RPC arm protocol; only the uniform limit is sourced from `data/knobs.json`. |

## How this map was checked

- `python3 -m pytest scripts/tests/test_retrieval_tuning_scopes.py -q` — emitters
  pinned byte-for-byte against the old spellings' strings, shim identity, loader
  forms, and the `custom`-accepts / `galaxy`-rejects validator pair.
- `grep -rn "scope IN ('project','custom')\|scope = 'shared'" scripts/retrieval_tuning
  scripts/src/retrieval_tuning` — remaining hits are docstrings and the two
  `scopes.py` clause builders only.
- `python3 -m pytest scripts/tests -q` before/after (full evidence in
  `docs/work/2026-09-10-p3-lane-report.md`).
