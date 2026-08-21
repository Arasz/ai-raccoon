# Retrieval parameter tuning report

- Run date: 2026-08-21
- Study: retrieval-tune (best trial 31)
- Eval corpus: eval-set-100.json (100 queries), dataset memory
- Drift check: PASS

## 1. Defaults vs best (eval set)

| metric | defaults | tuned | delta |
|---|---|---|---|
| mean nDCG@5 | 0.6105 | 0.6655 | +0.0550 |
| mean MRR@5 | 0.5677 | 0.6108 | +0.0432 |
| hit@3 rate | 0.6500 | 0.7300 | +0.0800 |
| hit@1 rate | 0.4700 | 0.4800 | +0.0100 |

Per-category breakdown (file-targeted vs non-file):

| bucket | config | count | mean nDCG@5 | mean MRR@5 | hit@3 rate | hit@1 rate |
|---|---|---|---|---|---|---|
| file | defaults | 75 | 0.5199 | 0.4669 | 0.5733 | 0.3467 |
| non-file | defaults | 25 | 0.8825 | 0.8700 | 0.8800 | 0.8400 |
| file | tuned | 75 | 0.5671 | 0.4984 | 0.6533 | 0.3333 |
| non-file | tuned | 25 | 0.9607 | 0.9480 | 0.9600 | 0.9200 |

## 2. Per-query regression table (tuned vs defaults, nDCG@5)

14 of 100 eval queries regress at the tuned config.

| entry_id | query | defaults nDCG@5 | tuned nDCG@5 | delta |
|---|---|---|---|---|
| E048 | What did the rating fix delete from the codebase, and why must the SQLite build provide the pow function? | 1.0000 | 0.3869 | -0.6131 |
| E028 | Is there an MCP tool that returns the full content of a memory entry by hash? | 1.0000 | 0.5000 | -0.5000 |
| E049 | What happened when a mistyped CLI verb fell through to the proxy and reached the production install? | 1.0000 | 0.5000 | -0.5000 |
| E002 | How does the dual-vector structure signal work — a second embedding of the heading path fused with the content embedding at a fixed alpha? | 1.0000 | 0.6309 | -0.3691 |
| E047 | Is the rating now computed in the same UPDATE statement that increments the access count? | 1.0000 | 0.6309 | -0.3691 |
| E051 | Why does a known verb with a wrong argument keep exit code 15 instead of the parse-failure code? | 1.0000 | 0.6309 | -0.3691 |
| E052 | Why were long memory_write bodies stored as one row with most of the tokens never embedded? | 1.0000 | 0.6309 | -0.3691 |
| E074 | Is there now one resolved record per search with precedence query over settings over constants? | 0.6309 | 0.4307 | -0.2003 |
| E037 | Why was the section column's FTS bm25 weight 16, and was that weight ever actually exercised on a real bank? | 0.6309 | 0.5000 | -0.1309 |
| E039 | Which ranking gates moved when the section weight dropped, and what is wrong with the A1 relevance label? | 0.6309 | 0.5000 | -0.1309 |
| E050 | Which exit code does an unrecognised CLI verb now produce instead of launching the proxy? | 0.5000 | 0.3869 | -0.1131 |
| E007 | How does the bank schema migrate today — does EnsureAsync use a version marker or per-feature existence probes? | 0.5000 | 0.4307 | -0.0693 |
| E064 | What is the project's stance on supporting very long pasted queries? | 0.5000 | 0.4307 | -0.0693 |
| E016 | Why does AiRaccoon.Core need the System.Numerics.Tensors package — what does the mean-pool-and-normalize kernel do? | 0.4307 | 0.3869 | -0.0438 |

> **FLAG**: more than 5 eval queries regress on nDCG@5 vs defaults — owner review of this table is required before shipping (plan §10).

## 3. Test-set grade deltas (3-level: good / could-be-improved / just-wrong)

| entry_id | query | default grade | tuned grade | delta |
|---|---|---|---|---|
| TS-01 | what score did the second ranked project tier entry get after max normalization | could-be-improved | good | +1 |
| TS-02 | What were the mean nDCG@5 numbers for in-sample vs held-out queries? | good | good | +0 |
| TS-03 | How often does the vacuum maintenance job run? | good | could-be-improved | -1 |
| TS-04 | Which gate was reddened when the settings read was removed? | good | good | +0 |
| TS-05 | a tier's best entry and the corpus's best entry were arithmetically indistinguishable | good | good | +0 |
| TS-06 | the no-fusion-regression rule is an order, not a score | good | good | +0 |
| TS-07 | maintenance is a list of jobs, and the schedule lives in the bank | good | good | +0 |
| TS-08 | What were the results of the full test suite run after removing the promotion classifier? | could-be-improved | just-wrong | -1 |
| TS-09 | What was the mean cosine similarity diagnostic for the noise classifier? | good | good | +0 |
| TS-10 | How is local development orchestrated for new jsaa projects? | just-wrong | just-wrong | +0 |

Summary: **1 improved**, **2 worsened**, **7 unchanged**.

> Caveat: curated default grades were recorded at the copy's INHERITED settings (fusion=true, structureAlpha=0.5 leak, plan §1); tuned grades are observed live at the explicit tuned config.

## 4. Matrix influence summary (per knob, from the matrix CSV)

Per dataset: each knob's mean nDCG@5 across its ladder, Δ (max − min) and the best ladder value (other knobs held at the explicit defaults).

### Dataset: memory

| knob | ladder values | nDCG@5 by value | ΔnDCG5 | best value |
|---|---|---|---|---|
| candidateWindow | max3x100, max5x50 | 0.611, 0.602 | 0.0090 | max3x100 |
| consolidationThreshold | 0, 0.05, 0.1, 0.2, 0.5, 1.0 | 0.478, 0.580, 0.611, 0.595, 0.569, 0.552 | 0.1327 | 0.1 |
| docScoreFormula | max, sum | 0.611, 0.611 | 0.0000 | max |
| ftsWeight | 0, 1, 2, 3, 5, 10 | 0.424, 0.611, 0.619, 0.602, 0.599, 0.599 | 0.1956 | 2 |
| fusion | False, True | 0.611, 0.554 | 0.0561 | False |
| rrfK | 1, 5, 15, 60, 120, 200 | 0.593, 0.624, 0.635, 0.611, 0.557, 0.521 | 0.1137 | 15 |
| sourceLambda | 0, 0.05, 0.1, 0.2, 0.3, 0.5 | 0.616, 0.619, 0.611, 0.523, 0.501, 0.482 | 0.1372 | 0.05 |
| structureAlpha | 0, 0.25, 0.5, 0.75, 1.0 | 0.318, 0.491, 0.611, 0.599, 0.612 | 0.2936 | 1.0 |
| vectorWeight | 0, 1, 2, 3, 5, 10 | 0.589, 0.611, 0.587, 0.565, 0.542, 0.520 | 0.0906 | 1 |

### Dataset: sextant

| knob | ladder values | nDCG@5 by value | ΔnDCG5 | best value |
|---|---|---|---|---|
| candidateWindow | max3x100, max5x50 | 0.655, 0.655 | 0.0000 | max3x100 |
| consolidationThreshold | 0, 0.05, 0.1, 0.2, 0.5, 1.0 | 0.772, 0.855, 0.655, 0.594, 0.594, 0.594 | 0.2616 | 0.05 |
| docScoreFormula | max, sum | 0.655, 0.655 | 0.0000 | max |
| ftsWeight | 0, 1, 2, 3, 5, 10 | 0.750, 0.655, 0.655, 0.636, 0.636, 0.629 | 0.1210 | 0 |
| fusion | False, True | 0.655, 0.560 | 0.0949 | False |
| rrfK | 1, 5, 15, 60, 120, 200 | 0.833, 0.938, 0.938, 0.655, 0.572, 0.572 | 0.3667 | 5 |
| sourceLambda | 0, 0.05, 0.1, 0.2, 0.3, 0.5 | 0.938, 0.833, 0.655, 0.594, 0.594, 0.594 | 0.3449 | 0 |
| structureAlpha | 0, 0.25, 0.5, 0.75, 1.0 | 0.503, 0.655, 0.655, 0.655, 0.655 | 0.1521 | 0.25 |
| vectorWeight | 0, 1, 2, 3, 5, 10 | 0.629, 0.655, 0.644, 0.644, 0.644, 0.644 | 0.0262 | 1 |

## 6. Eval floor gate (G4 discrimination)

- Floor: mean_ndcg5 >= 0.5
- Defaults config: PASS
- Tuned config: PASS

Sources: matrix CSV, tuned-parameters JSON, optuna study DB, corpora.
