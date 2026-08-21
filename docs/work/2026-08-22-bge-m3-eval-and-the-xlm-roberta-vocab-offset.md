# bge-m3 retrieval comparison — and the defect it found

**Date:** 2026-08-22
**Bank:** a frozen copy of the live bank (23,401 entries), re-embedded to bge-m3 1024-d
**Corpora:** `scripts/retrieval_tuning/corpora/{eval-set-100,test-set-10}.json`
**Relates to:** ADR-0084 (manifest-described engines), `docs/work/2026-08-22-retrieval-tune-no-regression.md` (M2)

## Summary

The 2×2 (bge-m3 × {defaults, M2}) ran to completion, and its headline number is **not a
model comparison**. bge-m3 scored roughly half of MiniLM on every arm. Chasing that led to a
real product defect: **the sentencepiece path fed xlm-roberta models raw sentencepiece token
ids, which are off by one against the vocabulary the model was trained on.** Every token read
the wrong embedding row.

So the transferability question this run was meant to answer — does the M2 tuning generalise
to another model? — **remains open**. The fix is in this PR; the re-measurement is not.

## What was measured

| engine | tuning | corpus | nDCG@5 | MRR@5 | hit@3 | hit@1 |
|---|---|---|---|---|---|---|
| MiniLM-384 | defaults | eval-set-100 | 0.5631 | 0.5237 | 0.630 | 0.420 |
| MiniLM-384 | defaults | test-set-10 | 0.7448 | 0.6950 | 0.700 | 0.600 |
| MiniLM-384 | M2 | eval-set-100 | 0.5924 | 0.5395 | 0.690 | 0.410 |
| MiniLM-384 | M2 | test-set-10 | 0.7023 | 0.6233 | 0.700 | 0.500 |
| bge-m3-1024 | defaults | eval-set-100 | 0.2847 | 0.2455 | 0.350 | 0.130 |
| bge-m3-1024 | defaults | test-set-10 | 0.4079 | 0.3450 | 0.400 | 0.200 |
| bge-m3-1024 | M2 | eval-set-100 | 0.4760 | 0.4143 | 0.570 | 0.260 |
| bge-m3-1024 | M2 | test-set-10 | 0.4948 | 0.4283 | 0.500 | 0.300 |

Paired per-query, with an exact two-sided sign test (the mean delta alone is not readable at
n=10):

| comparison | corpus | mean ΔnDCG@5 | better | worse | tied | p |
|---|---|---|---|---|---|---|
| M2 vs defaults, MiniLM | eval-set-100 | +0.0293 | 18 | 11 | 71 | 0.265 |
| M2 vs defaults, MiniLM | test-set-10 | −0.0426 | 0 | 2 | 8 | 0.500 |
| M2 vs defaults, bge-m3 | eval-set-100 | +0.1914 | 38 | 5 | 57 | <0.001 |
| M2 vs defaults, bge-m3 | test-set-10 | +0.0869 | 4 | 2 | 4 | 0.688 |
| bge-m3 vs MiniLM, defaults | eval-set-100 | −0.2785 | 12 | 49 | 39 | <0.001 |
| bge-m3 vs MiniLM, M2 | eval-set-100 | −0.1164 | 15 | 34 | 51 | 0.009 |

**Two readings that correct earlier reporting:**

1. **The MiniLM test-set-10 "regression" against the M2 doc is not a regression.** It is 2
   changed queries out of 10, 8 tied, p=0.50 — indistinguishable from noise. An earlier note
   in this session called it a contradiction of the tuning doc's held-out result; the paired
   test does not support that.
2. **M2's apparent gain on MiniLM is also not significant** (p=0.265). The only significant
   M2 gain is on bge-m3 — and that is a symptom, not a finding: M2 raises `ftsWeight` above
   `vectorWeight`, so it *rescues* a broken vector leg. It is further evidence of the defect.

## The defect

**Symptom.** bge-m3's vectors carry almost no discriminative signal. Measuring cosine geometry
over 1,500 sampled entries from each bank:

| | dim | random-pair cos | same-doc | cross-doc | separation |
|---|---|---|---|---|---|
| MiniLM (live bank) | 384 | 0.248 (sd 0.112) | 0.428 | 0.247 | **+0.182** |
| bge-m3 (this run) | 1024 | 0.769 (sd 0.053) | 0.789 | 0.769 | **+0.020** |

A narrow, high-cosine cone with 9× less separation between same-document and unrelated chunks.

**Cause.** bge-m3 is an `XLMRobertaModel` with `vocab_size` **250,002**. Its
`sentencepiece.bpe.model` carries **250,000** pieces under its own numbering
(`<unk>`=0, `<s>`=1, `</s>`=2, `,`=3 …). The fairseq/HF vocabulary prepends `<s>`=0,
`<pad>`=1, `</s>`=2, `<unk>`=3 and shifts every ordinary piece by **+1** (`,`=4 …), with
`<mask>`=250001 at the end — which is exactly the 2-token difference.

`SentencePieceEmbeddingTokenizer` returned `ML.Tokenizers`' raw sentencepiece ids and nothing
downstream remapped them (`OnnxEmbeddingGenerator.cs` feeds `EncodeToIds` straight to the
graph). So the model received `,` as id 3 — which in its own vocabulary is `<unk>` — and every
ordinary token one row off. The output is deterministic and plausible-looking, never crashes,
and is semantically noise.

**How it survived review.** The existing test pinned the defect rather than catching it:

```csharp
// probed from ML.Tokenizers: bos=1, eos=2 for the bge-m3 onnx sentencepiece model
// — NOT the xlm-roberta vocab ids, which differ). The contract being pinned is
// "exactly two extra ids at the edges", whatever their values.
ids[0].ShouldBe(raw.BeginningOfSentenceId);
```

The discrepancy was seen, written down, and then the assertion was weakened to accept it. The
class doc said as much: *"Tokenizer parity against the HF fast tokenizer is out of WP3 scope."*
This is ADR-0084's own recorded lesson recurring one layer down — **fixtures derived from the
implementation cannot falsify it.**

## The fix

A manifest-declared `tokenizer.options.vocabOffset` (default **0**, so plain sentencepiece
models — T5, LLaMA — are unaffected). When non-zero, `SentencePieceEmbeddingTokenizer` maps the
three control pieces onto the ids the manifest declares and shifts every ordinary piece.
`ModelDownloadPlanner` derives it from `tokenizer_class`, narrowly: `XLMRoberta*` → 1.
Deliberately not the broader `Roberta` predicate already used for bos/eos — plain RoBERTa is
byte-level BPE and never reaches this path, and other fairseq ports (CamemBERT) use different
offsets and must be hand-written.

Because ADR-0084 D7 makes the manifest's content part of the engine fingerprint, adding the
field to an existing model directory re-embeds that bank on its own.

## What is proven, and what is not

**Proven.** The token ids are wrong without the fix and correct with it
(`SentencePieceVocabParityTests`, RED before / GREEN after; the planner derivation is
mutation-proven). The embedding geometry above is measured, not inferred.

**Not proven.** That the fix restores bge-m3's retrieval quality. That needs a re-embed of the
bank (~3.4 h) and a re-run of this matrix, and it has not been done. **No claim about bge-m3
vs MiniLM, or about M2 transferability, should be drawn from the numbers above** — they
measure a broken tokenizer.

**Gap this leaves.** The G3 golden gate covers token-id equality for the bundled *wordpiece*
tokenizer only. Sentencepiece had no reference-parity gate, which is why an off-by-one across
the whole vocabulary reached a shipped release. A parity fixture against known-good HF ids for
a handful of strings would have caught it at WP2.
