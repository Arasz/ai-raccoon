# Real-world retrieval corpus design (for embedding benchmarks)

How to build a benchmark corpus that actually discriminates between embedding models — learned while building the ai-raccoon benchmark (48-doc synthetic set upgraded to 174 real docs).

## Why synthetic corpora fail

A small topic-clustered synthetic corpus (8 topics x 5 docs, 16 queries, each query judged relevant to its own topic's docs) gave EVERY model R@10 = 1.0, MRR = 1.0, nDCG ≈ 0.998 — a ceiling. Distinctive invented vocabulary means keyword
overlap trivially solves every query, so no embedding model can be ranked. The real-world corpus dropped scores to R@5 ~0.33, MRR 0.84–0.86 and exposed a real nDCG gap (0.61 local vs 0.70 EmbeddingGemma).

## Source selection

Pick docs the user actually works with: ADRs, architecture docs, invariants, skills, READMEs, and agent-memory notes (`.remember/`). For the ai-raccoon benchmark: ~85 docs from job-search-ai-assistant (`docs/adr/`, reference docs),
~85 from ai-badger (`docs/adr/`, `features/common/invariants/`, skills), ~50 from arasz-home-page (`docs/adr/`, architecture docs), ~20 from `.remember/`
notes. Include near-duplicate pairs deliberately (same ADR in a worktree copy)
as hard cases.

- Sanitize: strip secrets, tokens, connection strings, emails, account ids. Concepts are fine ("fine-grained PAT", "Key Vault"); literals are not.
- Cap bodies at 2–4 sentences (~150–220 words) of verbatim text from Context/Decision/Consequences sections.
- SKIP `logs/` dirs in `.remember/` — commands and token counts live there.

## Honest ground-truth query generation

Every query needs verified RelevantDocIds. Three mechanisms, roughly 30% easy / 45% medium / 25% hard:

1. **Doc-derived (easy)**: restate a doc's own heading/decision as a question ("Which library is used for domain validation?" from ADR-0001). Relevant = that doc + any other doc verified to cover the same fact. Sample every Nth doc so easy
   queries don't dominate.
2. **Cross-repo clusters (medium)**: one topic, docs from >= 2 repos (e.g. "ai-badger adoption" appears in jsaa ADR-0019/0020 and home ADR-0004). Verify by keyword-scanning the corpus for the topic's distinctive terms and reading every
   hit — add docs that genuinely cover it, exclude docs that merely mention it (keyword matches are NOT relevance; this kills false positives AND false negatives).
3. **Agent-memory-derived (hard)**: restate a `.remember` note line as a question; relevant = the artifact the note cites. Deliberately avoid the doc's distinctive token pair in the query so keyword overlap doesn't trivialize it.

**Critical: exclude daily agent-memory notes from relevance sets.** Notes (`today-*.done.md`, `recent.md`) *mention* many topics but don't *cover*
decisions. Including them makes every query list 15+ relevant docs and hits the ceiling again. Notes are query sources, never cluster members. Also drop docs judged non-relevant despite keyword overlap (e.g. an auth ADR about Easy Auth
ordering is not relevant to a GitHub-identity question even though both contain "auth").

## Mechanics

- Record each query's verification as a `// judgment: ...` comment in the generated C# — the ground truth must be auditable.
- Validate generated corpora: every RelevantDocId exists in Documents; no query has an empty relevance set (evaluators silently score 0 on those).
- A generator script (Python) that reads the source repos read-only and emits the C# corpus files is the right shape: reproducible, idempotent (two sequential runs byte-identical), and reviewable. Regenerate deliberately, not on every
  benchmark run.
- Target ~150–400 docs and 30–80 queries; ~55k embedding tokens per run is trivially cheap for local GGUF and LM Studio.

## Metrics that stay honest

Recall@k (relevant∩topK / relevant), MRR (1/ (first-relevant-rank+1)), nDCG@k (DCG/IDCG with binary gain and log2 discount) — the standard IR definitions. Report Recall@5/Recall@10/MRR/nDCG@10 over top-10 retrieval, averaged per query, plus
BenchmarkDotNet latency per backend. A console table + non-zero exit on failure is a CI-friendly shape.
