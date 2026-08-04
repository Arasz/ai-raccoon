# Baseline Retrieval Report

> **Project:** jsaa | **Generated:** 2026-08-04T10:36:17Z
> **Test:** `RunAllBaselineQueries_ReportsMatchStatistics` | **Method:** sqlite-memory hybrid search (BM25 + vector)
> **Seeded:** 681 chunks from 166 files

---

## Executive Summary

| Metric | Value |
|--------|-------|
| Total queries | 35 |
| Queries with results | 35 (100%) |
| Expected-source queries | 10 |
| Matched at rank ≤3 | **6** |
| Match rate | **60.0%** |

The retrieval pipeline achieves 60% recall for expected-source queries at rank ≤3.
Invariants & Conventions queries are perfectly matched. Architecture Decision Records (ADR)
queries have more variance — some ADRs are chunked into sections that compete with similar content.

---

## Category Breakdown

| Category | Queries | Results | Matched Top3 | Match Rate |
|----------|---------|---------|--------------|------------|
| Architecture Decisions (ADR) | 7 | 7 | 3 | 43% |
| Cross-Cutting (Multi-Context) | 3 | 3 | 0 | N/A |
| Domain Rules (Seed Data) | 3 | 3 | 0 | N/A |
| Invariants & Conventions | 6 | 6 | 3 | 100% |
| Negative Tests | 3 | 3 | 0 | N/A |
| Project History & Identity | 3 | 3 | 0 | N/A |
| Skills & Agent Workflows | 4 | 4 | 0 | N/A |
| System Architecture & Knowledge | 6 | 6 | 0 | N/A |

---

## Matched Queries (6/10)

### + QA1 — Architecture Decisions (ADR)
- **Query:** "Why was shadcn/ui chosen over gluestack.io?"
- **Expected:** `docs:adr:0011-frontend-chassis-stack.md#decision`
- **Found at rank 2**

### + QA2 — Architecture Decisions (ADR)
- **Query:** "What ADR governs UUID choice?"
- **Expected:** `docs:adr:0004-uuid-version7-for-identifiers.md#decision`
- **Found at rank 3**

### + QA5 — Architecture Decisions (ADR)
- **Query:** "What replaced the LLM cost NFR?"
- **Expected:** `docs:adr:0046-retire-nfr-1-llm-cost-protection.md#decision`
- **Found at rank 3**

### + QC1 — Invariants & Conventions
- **Query:** "Is TDD required?"
- **Expected:** `ai-badger:invariants/tdd-mandatory.md`
- **Found at rank 1**

### + QC2 — Invariants & Conventions
- **Query:** "What is the screaming architecture rule?"
- **Expected:** `ai-badger:invariants/screaming-architecture.md`
- **Found at rank 1**

### + QC5 — Invariants & Conventions
- **Query:** "Are hardcoded secrets allowed?"
- **Expected:** `ai-badger:invariants/no-hardcoded-secrets.md`
- **Found at rank 1**

---

## Missed Queries (4/10)

### - QA3 — Architecture Decisions (ADR)
- **Query:** "How does the project handle offer-page fetching security?"
- **Expected:** `docs:adr:0006-client-side-offer-page-fetch.md#decision`
- **Status:** found at rank 4 (just outside top-3)

### - QA4 — Architecture Decisions (ADR)
- **Query:** "What happened to the MCP server?"
- **Expected:** `docs:adr:0060-delete-the-mcp-server.md#decision`
- **Status:** found at rank 5

### - QA6 — Architecture Decisions (ADR)
- **Query:** "How does the project handle data erasure?"
- **Expected:** `docs:adr:0067-registry-driven-erasure-with-runtime-verification.md#decision`
- **Status:** NOT FOUND in results

### - QA7 — Architecture Decisions (ADR)
- **Query:** "What is ADR-0070 about?"
- **Expected:** `docs:adr:0070-documentation-structure-and-trust-model.md#decision`
- **Status:** NOT FOUND in results

---

## Rank Distribution

| Rank | Count |
|------|-------|
| 1 | 3 |
| 2 | 1 |
| 3 | 2 |
| 4 | 1 |
| 5 | 1 |

8 of 10 expected sources are found somewhere in results; 6 of those at rank ≤3.

---

## Negative Tests (25 queries)

These queries lack an expected source — they test that results are generally relevant.
All 25 returned at least one result.

| ID | Category | Query |
|----|----------|-------|
| QB1 | System Architecture | "What are the core architectural principles?" |
| QB2 | System Architecture | "What Azure services does the project use?" |
| QB3 | System Architecture | "How does local development work?" |
| QB4 | System Architecture | "What is the Cosmos partition key strategy?" |
| QB5 | System Architecture | "What are the extension points?" |
| QB6 | System Architecture | "How does authentication work?" |
| QC3 | Invariants | "How should NuGet packages be managed?" |
| QC4 | Invariants | "What logging pattern is required?" |
| QC6 | Invariants | "What error format does the API use?" |
| QD1 | Skills & Agents | "How does the task orchestration skill work?" |
| QD2 | Skills & Agents | "What agents are available?" |
| QD3 | Skills & Agents | "What model does the architect use?" |
| QD4 | Skills & Agents | "How do prompt markers work?" |
| QE1 | Domain Rules | "What is ATS-001?" |
| QE2 | Domain Rules | "What are the three layers of modern hiring screens?" |
| QE3 | Domain Rules | "What ATS rules govern keyword usage?" |
| QF1 | Project History | "What major work happened in week of 2026-07-27?" |
| QF2 | Project History | "When was the project established?" |
| QF3 | Project History | "What identity patterns does the project exhibit?" |
| QG1 | Cross-Cutting | "How is compensation calculated?" |
| QG2 | Cross-Cutting | "What is the frontend technology stack?" |
| QG3 | Cross-Cutting | "How does channel monitoring work?" |
| QH1 | Negative Tests | "What is in state.json?" |
| QH2 | Negative Tests | "What happened today?" |
| QH3 | Negative Tests | "Show me the Aspire config" |

---

## Key Observations

1. **Invariants are perfectly retrieved** — short, keyword-dense documents (`tdd-mandatory.md`, `screaming-architecture.md`, `no-hardcoded-secrets.md`) match cleanly at rank 1.

2. **ADR retrieval struggles with long documents** — ADRs are chunked by heading sections (header, decision, consequences, etc.). Queries targeting a specific section compete with sibling sections of the same document and sections of other ADRs on similar topics.

3. **ADR-0067 and ADR-0070 not found** — These are newer ADRs; their content may not have strong keyword overlap with the query phrasing.

4. **All 35 queries return results** — 100% coverage is good; the pipeline consistently surfaces relevant content even when the exact expected chunk isn't in the top 3.

5. **8/10 expected sources exist in results** — two miss entirely, and two more sit just outside top-3 at ranks 4-5.
