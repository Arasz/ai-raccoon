# Research: why the probe found no served waste in a redundant bank

**Date:** 2026-09-08
**Question:** Why did the P0a probe find (almost) no served cross-source near-dupe waste at band ≥0.95 when the bank contains 45,953 pairs at cosine ≥0.85?

```chart:bars
title: pairs at cosine 0.85+ by band (P0a scan, all classes)
0.85-0.90: 20018
0.90-0.95: 15690
0.95-0.98: 4974
0.98-0.99: 865
0.99-1.00: 4406
```

```chart:range
title: served top-8 max-pair-cosine across 60 lists (recomputed from copy)
served lists: 0.592..0.789..0.885
```

## Findings

### F1 — Only 22% of the "redundant" pairs sit in the band the rule counts [READ]

The 45,953 pairs ≥0.85 split 20,018 / 15,690 / 4,974 / 865 / 4,406 across the five bands,
so 35,708 pairs (77.7%) live at 0.85–0.95 where the pre-registered ≥0.95 bar never looks.
The headline number and the verdict number were different populations from the start:
"the corpus is redundant" was measured at ≥0.85, "no waste" at ≥0.95.

**Evidence:** `docs/work/2026-09-08-mmr-bank-redundancy-probe.md` §3 band table; raw branch
artifact `p0a-scan/pairs.json` (`bandCross`/`bandSame`/`bandNull`).

### F2 — Of the ≥0.95 remainder, over half is same-file and nearly three-quarters of the cross-source rest sits at 0.99–1.00 [MEASURED]

Recomputed from the branch artifact: ≥0.95 total 10,245 (22.3% of 45,953); cross-source
≥0.95 4,642, of which band 0.99–1.00 holds 3,355 (72.3%); same-file ≥0.95 5,565 (54.3% of
the ≥0.95 set). So after the bar, the pool MMR could actually address — cross-source,
below-exact, co-retrievable — is a small slice of a slice: same-file pairs belong to
consolidation's territory, and the 0.99+ cross pairs are the byte-identical-mirror
suspects (see F4/F6).

**Evidence:** `git show origin/task/air-mmr-impl-p0a:p0a-scan/pairs.json` fetched 3×
(md5 `3243d728…` identical all three), band-share arithmetic run on Apple M4 over the
fetched bytes; totals reproduce the report's 45,953 exactly.

### F3 — Served top-8 lists structurally live below 0.95: min 0.592, p50 0.789, max 0.885, identical across three runs [MEASURED]

Recomputed max-pair-cosine for each of the 60 sampled top-8 lists from the lane's bank
copy: 0/60 lists reach ≥0.95, 5/60 reach ≥0.85 — exactly the report's §4 distribution,
bit-identical all three runs. A top-8 can only waste a slot on a pair at least as similar
as its own maximum; with the served maximum at 0.885, a ≥0.95 hit requires an outlier list
the sample never drew. The 0/60 is what the served distribution predicts, not a surprise
inside it.

**Evidence:** `python3 /tmp/mmr-maxpair.py` ×3 on Apple M4, Python 3.14.7, read-only
(`mode=ro`) against `/tmp/mmr-bank-copy/p0a/memory.db`, hashes from branch
`p0a-scan/sample.json` (60 lists, 300 hashes, 297 embedded); 4096-byte float32 blobs,
cosine with norm division; run outputs byte-identical (min/p50/max 0.592/0.789/0.885).

### F4 — Byte-identical copies never reach served lists: pre-fusion content dedupe eats them [READ]

`ModalityCandidates.Deduplicated` groups each leg's candidates by content
(`ContentHash.OfValue(value)`), keeping one row per identical text with the project copy
preferred over `shared/` — before RRF fusion ever sees them. The report's raw==faithful
on all 240 served lists is the downstream shadow of this: collapse "never fires" on served
lists because the pipeline already removed everything collapsible upstream. This is also
why the 0.99–1.00 cross band (F2's 3,355) barely surfaces: exact live↔archive mirrors are
the dedupe's exact prey; the single served ≥0.95 case (0.9994, near- but not byte-identical)
is what slips a content-equality sieve.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/Memory/ModalityCandidates.cs:26-55`
(doc comment + grouping); report §4 (`raw == faithful`, solitary 0.9994 mirror).

### F5 — There is no paradox: four stacked filters turn 45,953 pairs into 0 served hits [INFERRED]

Reasoning from F1–F4: (1) the bar discards 77.7% of pairs (0.85–0.95 uncounted); (2) of the
remainder, 54.3% is same-file (consolidation's, not MMR's); (3) most cross-source ≥0.95
sits at 0.99+, where byte-identity meets the pre-fusion dedupe; (4) whatever survives must
still co-retrieve — both members ranking inside one top-8 — and the served distribution
(F3) shows pairs that similar essentially never share a served list. Each filter is
independently evidenced; their composition is the inference. The NO-GO verdict is therefore
not "MMR found nothing despite redundancy" but "the pre-registered waste population —
cross-source, below-exact, co-retrieved, ≥0.95 — is nearly empty on this bank", which is a
finding about the bank, not a failure of the probe. The 0.85–0.95 band (26.7–31.7% re-served
hit rate) is where the corpus's real redundancy lives, and the plan's re-open rule names
exactly that.

### F6 — Whether the 0.99–1.00 cross pairs are byte-identical is unchecked [UNVERIFIED]

`pairs.json` carries band aggregates only, no per-pair hashes or values, so the "exact
mirrors eaten pre-fusion" half of F5 cannot be sized from available artifacts. Settleable
read-only on the surviving copy: re-block one file pair, compare bytes where cosine ≥0.99,
count identical. Not run — the copy still exists at `/tmp/mmr-bank-copy/p0a/`, but the
question as asked is answered without it, and every re-run risks stating what the report
already pinned.

## Still open

- Per-pair exactness at 0.99–1.00 (F6): sizes filter 3 and tells a revival exactly what content class to target.
- Whether the 60-query sample covered the bank's redundant topics: the probe sampled served-log queries across 5 projects/60 families, but no one checked topic overlap with the archive-mirror clusters that dominate the pair table.
- The 0.85–0.95 re-open case stands as the plan recorded it — this record does not re-register anything, it only explains why the ≥0.95 verdict came out empty.
