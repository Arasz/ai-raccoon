# features/

Feature dossiers and behavioural contracts. This directory predates the canonical
Diátaxis tree (see `docs/README.md`); each subdirectory is one feature.

## Contents

| Feature                            | Status                                  | Dossier                                                                            |
|------------------------------------|-----------------------------------------|------------------------------------------------------------------------------------|
| [`file-watcher/`](file-watcher/)   | Implemented (merged to main 2026-08-05) | [spec.json](file-watcher/spec.json) — watch a path and mirror FS changes into memory (3 MCP tools + `watch` CLI verbs) |
| [`encryption-bitwarden/`](encryption-bitwarden/) | Implemented                             | [spec.json](encryption-bitwarden/spec.json) — encryption key sources: env (default, kept) + Bitwarden Secrets Manager via `bws` CLI (`encryption bitwarden`/`show`/`unset` CLI verbs) |

Earlier wave dossiers (agent-memory, native-memory) live under
[`docs/work/features-*`](../work/) with their Gherkin behavioural contracts
(`agent-memory.feature`, 29 scenarios; `native-memory.feature`) and `spec.json`
manifests consumed by the ai-badger task flow.
