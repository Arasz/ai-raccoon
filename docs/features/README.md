# features/

Feature dossiers and behavioural contracts. This directory predates the canonical
Diátaxis tree (see `docs/README.md`); each subdirectory is one feature.

## Contents

| Feature                            | Status                                  | Dossier                                                                            |
|------------------------------------|-----------------------------------------|------------------------------------------------------------------------------------|
| [`agent-memory/`](agent-memory/)   | Implemented (merged to main 2026-08-03) | [spec-issue-1.md](agent-memory/spec-issue-1.md)                                    |
| [`native-memory/`](native-memory/) | Implemented (merged to main 2026-08-04)  | [spec.json](native-memory/spec.json) — managed .NET memory store (own SQLite schema, Dapper queries, C# RRF, ONNX embeddings, S3 sync) replacing the pinned sqlite-memory extension |

Each dossier's folder also carries its Gherkin behavioural contract
(`agent-memory.feature`, 29 scenarios) and the `spec.json` manifest consumed by the
ai-badger task flow.
