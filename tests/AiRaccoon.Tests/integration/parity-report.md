# Retrieval parity report (P6 FR-NM-5)

- Generated: 2026-08-03 22:13:09 +02:00 on Unix 26.5.2 (Arm64)
- Reference oracle: sqlite-memory-1.3.5+sqlite-vector-1.0.0, model all-MiniLM-L6-v2.Q5_K_M.gguf (SHA-256 908c82ac3849…), golden k=10, 174 docs, 68 graded queries
- New side: managed store (FTS5 + vec0), bundled int8 ONNX all-MiniLM-L6-v2, RRF fusion, rank depth 60, minScore 0 (full capture)
- Gate: one-sided 'no regression' — new-side nDCG@10 must not fall more than 0.02 below the reference at any sweep point. Positive Δ means the new side exceeds the reference (the sweep's purpose); the full sweep matrix is recorded here.

## Reference (vendored golden, k=10 window)

| nDCG@10 | nDCG@20* | MRR | Recall@10 |
|---|---|---|---|
| 0,6251 | 0,5298 | 0,8903 | 0,3769 |

*nDCG@20 is computed over the golden's k=10 window and is a lower-bound reference.

## Sweep matrix (new side), Δ = new-side nDCG@10 − reference nDCG@10

| point | nDCG@10 | nDCG@20 | MRR | Recall@10 | Recall@30 | Δ nDCG@10 |
|---|---|---|---|---|---|---|
| k10-w11 | 0,6639 | 0,6575 | 0,8716 | 0,4152 | 0,5302 | +0,0388 |
| k10-w12 | 0,6353 | 0,6428 | 0,8571 | 0,3971 | 0,5249 | +0,0102 |
| k10-w21 | 0,6880 | 0,6821 | 0,9134 | 0,4167 | 0,5262 | +0,0629 |
| k30-w11 | 0,6693 | 0,6649 | 0,8760 | 0,4063 | 0,5300 | +0,0442 |
| k30-w12 | 0,6540 | 0,6416 | 0,8622 | 0,3967 | 0,5087 | +0,0289 |
| k30-w21 | 0,6868 | 0,6782 | 0,8878 | 0,4193 | 0,5300 | +0,0617 |
| k60-w11 | 0,6719 | 0,6628 | 0,8756 | 0,4054 | 0,5125 | +0,0468 |
| k60-w12 | 0,6647 | 0,6568 | 0,8618 | 0,4022 | 0,5078 | +0,0396 |
| k60-w21 | 0,6857 | 0,6712 | 0,8830 | 0,4092 | 0,5331 | +0,0606 |

## Per-query audit at the default config (k60, weights 1:1)

For each graded query: new-side nDCG@10 minus reference nDCG@10. Queries where the new side is worse by more than 0.02 are true regressions; everything else is parity or better.

- Queries with new side better than reference by > 0.02: 30
- Queries within ±0.02 of the reference: 29
- Queries with new side worse than reference by > 0.02 (regressions): 9
- Worst per-query regression: -0,2843 (doc-jsaa-adr-0011-frontend-chassis-stack)

### Regressing queries (default config), modality attribution

For each query where the new side falls more than 0.02 below the reference, the table shows reference nDCG@10, new-side nDCG@10 (default), and the new side with only one modality (FTS-only = weights 1:0, vec-only = weights 0:1) to attribute the loss.

| query | reference | new (default) | FTS-only | vec-only |
|---|---|---|---|---|
| doc-badger-adr-0010-stack-local-skill-discovery | 0,5599 | 0,4734 | 0,2935 | 0,5479 |
| doc-badger-adr-0013-what-the-mcp-tool-index-is-for | 0,5366 | 0,5107 | 0,5836 | 0,4698 |
| doc-home-adr-0013-ci-cost-per-job-minimum-and-local-pre-push-gate | 0,5798 | 0,4374 | 0,5704 | 0,5036 |
| doc-home-remember-today-2026-07-30-done | 0,2985 | 0,2279 | 0,4073 | 0,0000 |
| doc-jsaa-adr-0005-jsonschema-net-generation-in-domain | 0,7165 | 0,5353 | 0,6746 | 0,6938 |
| doc-jsaa-adr-0011-frontend-chassis-stack | 0,7412 | 0,4569 | 0,5442 | 0,5232 |
| doc-jsaa-adr-0085-used-cv-bytes-are-encrypted-and-deduplicated-per-owner | 0,7472 | 0,4944 | 0,6115 | 0,6902 |
| doc-jsaa-doc-domain-glossary | 0,4885 | 0,4666 | 0,4600 | 0,5077 |
| doc-jsaa-remember-today-2026-07-20-done | 0,2201 | 0,0851 | 0,1370 | 0,0000 |

## Latency (new side, per query)

| p50 | p95 | max | samples |
|---|---|---|---|
| 5,9 ms | 14,4 ms | 257,4 ms | 715 |
