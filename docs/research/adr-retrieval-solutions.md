# ADR Retrieval: Section-Level Chunk Competition

**Date**: 2026-08-04 **Method**: First Principles (Aristotle's *protai archai*)
**Status**: Exploration — solution ideas, not yet evaluated

## Problem

ADR documents are chunked by heading sections (header, decision, consequences, etc.). Queries targeting a specific
section compete against:

1. **Sibling sections** of the same document (e.g., "Consequences" section competes with "Decision" section)
2. **Sections of other ADRs** on similar topics

Flat embedding space loses document identity and section-type information.

## Obvious Ideas Refused

| Idea                                     | Why Rejected                                                                                                        |
|------------------------------------------|---------------------------------------------------------------------------------------------------------------------|
| Add document ID as metadata filter       | Solves cross-document confusion but not sibling competition; ADR-007's Consequences still fights ADR-007's Decision |
| Use larger chunks (full ADR)             | Trades precision for context; model has to find the needle in 2,000 tokens                                          |
| Fine-tune an embedding model on ADR data | Expensive, fragile, doesn't address the structural problem — topology is the issue, not vocabulary                  |
| Hybrid BM25 + vector                     | BM25 loves exact term matches; fails when query uses different words than the target section                        |
| ColBERT late interaction                 | Token-level matching is better but overkill for a problem that's fundamentally about lost structural information    |

## Core Assumptions (Conventional Chunked Retrieval)

All seven are conventional — none are physical or informational constraints:

| # | Assumption                                                            | Category     |
|---|-----------------------------------------------------------------------|--------------|
| 1 | Chunks are independent retrieval units competing in one flat space    | Conventional |
| 2 | Cosine similarity on a single embedding is the primary ranking signal | Conventional |
| 3 | Chunk boundaries are structural (headings)                            | Conventional |
| 4 | Retrieval is stateless — no document identity carried through         | Conventional |
| 5 | One embedding model encodes everything in one space                   | Conventional |
| 6 | Retrieval and reranking are architecturally separate passes           | Conventional |
| 7 | Relevance is symmetric                                                | Conventional |

## Solution Ideas

### 1. Dual-Index Section-Aware Retrieval (DISAR)

Split the vector store into N parallel indices — one per ADR section type (Decision, Context, Consequences,
Alternatives). At query time, classify the query's section intent (zero-shot), then search only the matching index.

**Why**: Sibling competition isn't a retrieval quality problem — it's a search-space design problem. Separate indices
restore the structure that chunking destroyed.

**Failure mode**: Cross-section queries need a multi-index merge step with deduplication. Query classifier can
misclassify — a query about "consequences of the decision to use Kafka" is asking about a decision, not a consequence
section.

**Effort**: ~week **Stack**: Modifies existing vector DB setup (Pinecone namespaces, pgvector partition keys, or
multiple Chroma collections)

---

### 2. Document-Anchor Retrieval with Inverse Cloze Task Scoring

Retrieve *documents* first using document-level embeddings (mean-pool sections or embed a summary). Then, within top-K
documents, score each section by inverse cloze: "does this query belong under this heading?" The section whose heading
best completes the query wins.

**Why**: Two-pass retrieval restores hierarchy — document identity is recovered before section-level competition begins.

**Failure mode**: Two-pass latency. Inverse cloze scoring requires a cross-encoder that understands ADR heading
semantics — a generic BERT reranker may not distinguish "Decision" from "Rationale" cleanly.

**Effort**: ~week **Stack**: Adds a cross-encoder reranking pass; no index changes needed

---

### 3. Section-Role Chunk Prefixing at Index Time

At indexing time, generate a compressed "role statement" and prepend it to chunk text before embedding:
*"[ROLE: Decision section of ADR-007: Database Selection — states what was chosen and why]"* followed by actual content.
The embedding model encodes both content and structural identity.

**Why**: The role statement is a high-signal, low-noise signal injected into embedding input. Even if chunk body sounds
like a different section type, the prefix anchors it correctly. Query remains unprefixed (asymmetric by design).

**Failure mode**: LLM call per chunk at index time = linear cost. Role statements may misrepresent nuanced sections.
Attention dilution if chunk body is long relative to prefix.

**Effort**: Days to prototype, weeks to productionize **Stack**: LLM call at index time; no retrieval-time changes

---

### 4. [GROUNDED] Query Rewriting with Section-Type Prompt Injection

Before retrieval, classify query into a section type (regex keywords + fallback LLM), then rewrite with a structured
prefix: *"You are searching the DECISION section of an ADR. Find: what database did we select?"* Embed and search the
rewritten query. No index changes, no reranking, no fine-tuning.

**Why**: Modern embedding models are trained on instruction-following data with retrieval-task patterns. A structural
prefix biases the query embedding toward the right region of latent space — if the model respects it.

**Failure mode**: Depends entirely on whether the embedding model respects structural prefixes. Test: embed "Decision:
we chose PostgreSQL" vs "Consequences: we chose PostgreSQL" and measure cosine distance. If close, this approach fails.
Query classification can also be wrong — "what were the knock-on effects of the Kafka decision" asks about consequences
despite containing "decision."

**Effort**: ~afternoon **Stack**: A regex + one LLM call before the embedding step; zero infra changes

---

## Chesterton's Fence

The flat-index assumption is load-bearing for *general* retrieval — it's the simplest thing that works across arbitrary
document collections. ADR retrieval breaks because ADRs have stronger structural identity than most documents. A blog
post or wiki page doesn't have standardized sections that compete with each other; an ADR does. The fence is real: for
general RAG, flat indexing is the right default. The rebuild is correct only for structured, sectioned documents where
section-type disambiguation matters more than topical similarity.
