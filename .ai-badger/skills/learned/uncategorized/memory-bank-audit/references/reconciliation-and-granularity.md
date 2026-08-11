# Counter-vs-row reconciliation & dump-matching traps (verified 2026-08-09)

## Granularity mismatch: counters count CHUNKS, buckets are keyed per FILE

When tool counters don't sum to DB row counts, check granularity and silent-absorb paths BEFORE blaming races or dedup semantics.

Measured on AiRaccoon promotion: 108 promoted chunks → 64 shared rows, **0 value-dup groups** — because `AddContentAsync` returns the existing row for any later chunk of an already-represented file path (the shared bucket is per
`shared/<file path>`; the promote counter overcounts by the absorbed chunks). The end state was CORRECT; only the accounting was wrong.

Diagnostic order:

1. `SELECT path, chunk_index, total_chunks FROM entries WHERE scope='shared'` — the per-file chunk coverage shows the granularity mismatch instantly (rows claiming
   `total_chunks=1` for files that had N are the fingerprint).
2. Value-dup group count (`GROUP BY value HAVING COUNT(*)>1`) — zero groups here rules out value-dedup as the explanation.
3. Match promoted hashes to landed rows by PATH, not by hash (shared rows are re-hashed:
   hash = SHA256 ('shared/' + path + value)).

## Truncated dump fields break exact matching — prefix-match instead

SQL dumps that store `substr(path,1,90)` make `'shared/' + path` equality checks fail for most long paths (a "missing rows" verdict from exact matching on truncated fields is an artifact, not a finding). Compare truncated-against-full with
prefix matching (`full.startswith(truncated)`), or re-query the full values. Same trap applies to any preview/truncation column used as a join key.
