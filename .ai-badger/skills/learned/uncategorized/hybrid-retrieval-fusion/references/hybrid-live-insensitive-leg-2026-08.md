# Live-but-query-insensitive vector leg (measured 2026-08-20, AiRaccoon bundled model)

A vector leg can be ALIVE yet add no discrimination: the exact semantic match ranks 3-4 while the SAME two unrelated generic short texts top every query — five queries of completely different intent, stable order. Mechanism: low-information
texts ("this guide walks through onboarding...") sit near the embedding centroid and win every RRF merge; pairwise stored cosines were tiny (0.01-0.42 on the 384-dim bundled model), a sparse space where ranking is query-position-dependent.

## Diagnostic probes

- **Alien-token probe (liveness):** query with no token present in ANY stored text — FTS matches nothing, so any returned row proves the vector leg is in play.
- **Query-insensitivity tell:** the alien-token query's top-k matches the content query's top-k → the vector leg contributes no per-query signal. A leg is 'dead' in the sense that matters when it cannot discriminate, not only when it
  returns nothing.
- **FTS-leg isolation:** `SELECT rowid, bm25(entries_fts) FROM entries_fts WHERE
  entries_fts MATCH '<common-token>'` shows what the FTS leg alone contributes — this run proved FTS ranked the true match 1st while the FUSED list was vector-dominated, pinning the blame on the vector leg without touching the search
  pipeline.
- **Vector inspection without a vec0-capable sqlite:** system sqlite lacks the vec0 module; read the shadow table `vec_entries_vector_chunks00` instead — ONE blob per 1024-vector block (384 float32s per vector), vector slots 0-based (entry
  rowid 1 = slot 0). Unused slots are all-zero: check the slot mapping before claiming an entry was never embedded (an off-by-one slice misread a healthy bank as 'entry never embedded').

## Corpus-specific verdicts

Same query + same model ranked #1 on a 3-4-entry corpus and #3-4 on an 8-entry corpus containing two generic short texts. Any rank claim must carry the corpus composition; prior 'hybrid works' verdicts on tiny corpora do not predict ranking
behaviour on denser ones. A checklist/harness item can honestly fail on ranking quality even when the vector leg demonstrably returns content.

## Session context (2026-08-20, ai-raccoon 1.27.2 manual checklist)

- Query 'a navigator's device for measuring angles to celestial bodies at night' ranked its exact-match sextant entry 4th (then 3rd), behind two unrelated guide.md chunks and the astrolabe entry.
- Alien-token query 'quantum espresso froth ratios' (zero FTS matches) returned 8 results with the SAME top-2 as the content queries — the query-insensitivity tell.
- Direct FTS check `MATCH 'the'` BM25-ranked the astrolabe 1st, proving the FTS leg sane and the vector leg the dominator.
- Stored vectors: 8/8 norm 1.0, pairwise cosine 0.01-0.42 — well-formed, no corruption.
- Same query family ranked #1 in the 1.27.0/1.21.0 checklist runs on 3-4-entry corpora.
- Checklist verdict: fail (ranking quality), accepted left null for the human; full record in docs/work/checklist/2026-08-20-release-1-27-2.json.
