# LinkedIn — "Dodaj projekt" (Add project) form content

Copy-paste values for each field of the Polish LinkedIn project form. All content is in English.
Form field labels shown as **Polish label (English)**.

---

## Nazwa projektu (Project name) — max 255 chars (61)

```
AiRaccoon — Persistent Memory MCP Server for AI Coding Agents
```

---

## Opis (Description) — max 2000 chars (~1,700)

```
AiRaccoon is an MCP (Model Context Protocol) server that gives AI coding agents persistent, project-scoped memory. Built with .NET 10 on a local-first SQLite store.

The problem: AI agents forget everything between sessions. Context gets re-explained, decisions get re-litigated, and hard-won project knowledge evaporates.

AiRaccoon fixes that. Agents store notes, decisions and code knowledge through MCP tools, then recall them with hybrid search that fuses SQLite FTS5 keyword retrieval with vec0 vector KNN results via Reciprocal Rank Fusion. Embeddings run locally with a bundled ONNX model (all-MiniLM-L6-v2), or against any OpenAI-compatible endpoint.

Highlights:
• 30+ MCP tools covering search, memory lifecycle, workspaces and maintenance
• Local-first storage with optional page-level ChaCha20 encryption (SQLite3MC)
• Workspace sandboxes — isolated outboxes that are consolidated or discarded
• A shared tier for curated cross-project facts, exempt from memory decay
• Retrieval-based rating with automatic TTL sweeps
• A separate code corpus with its own embedding engine
• Optional S3 / Azure Blob snapshot sync with optimistic locking
• Proxy + HTTP daemon architecture with loopback token auth and OpenTelemetry metrics

It works with any MCP-capable client — Claude Code, Hermes and IDE integrations — installed
with a single `dotnet tool install -g ai-raccoon`.

Engineering: strict TDD workflow with BDD (Reqnroll) specifications, architectural decision records for every significant design choice, CI build and publish pipelines, and per-release benchmarks. Shipped as a global .NET tool on NuGet, MIT licensed, currently v1.42.

Repo: github.com/Arasz/ai-raccoon
NuGet: nuget.org/packages/ai-raccoon
```

---

## Umiejętności (Skills) — add 5

Recommended five (LinkedIn taxonomy terms where they exist):

1. `.NET`
2. `C#`
3. `Model Context Protocol (MCP)` — if unavailable, use `Large Language Models (LLM)`
4. `SQLite` — if unavailable, use `Databases`
5. `Vector Databases` — if unavailable, use `Semantic Search` or `Retrieval-Augmented Generation (RAG)`

Good alternates if any slot is taken: `Artificial Intelligence (AI)`, `AI Agents`, `Software Architecture`, `API Development`, `OpenTelemetry`, `Microsoft Azure`.

---

## Multimedia — up to 50 items

Suggested items, in order:

1. **Link** — GitHub repository: `https://github.com/Arasz/ai-raccoon`
2. **Link** — NuGet package: `https://www.nuget.org/packages/ai-raccoon`
3. **Image** — screenshot of the architecture diagram (the Mermaid flowchart near the top of the README, rendered)
4. **Image** — screenshot of the README feature/capabilities table or the "What's new" release notes
5. **Image** — screenshot of `memory_search` returning results from an agent session (the strongest "does it work" visual)
6. **Document** — the README as PDF, if you want a self-contained artifact (optional)

---

## Dodatkowe informacje (Additional information)

| Field                                                                          | Value                                                                               |
|--------------------------------------------------------------------------------|-------------------------------------------------------------------------------------|
| **Pracuję obecnie nad tym projektem** (I am currently working on this project) | ✅ Checked — actively developed, latest release v1.42.1                             |
| **Data rozpoczęcia** (Start date)                                              | August 2026                                                                         |
| **Data zakończenia** (End date)                                                | Leave empty (still active)                                                          |
| **Współautorzy** (Contributors)                                                | Solo project — leave empty unless you want to credit a contact                      |
| **Związane z** (Associated with)                                               | Leave empty — personal/open-source project, not linked to a company or school entry |

---

## Notes

- The description above is ~1,720 characters; LinkedIn's counter shows `x/2000`. Trim the
  "Engineering:" paragraph first if it needs to fit a narrower limit.
- The name field counter is `x/255`, so the title has plenty of room.
- Dates: first commit in the repo is 2026-08-02 — use that as the start month if you want the
  date to match the repository history exactly.
