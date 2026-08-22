// AUTO-GENERATED from this repository's own public docs; each query carries a
// // judgment comment documenting how its relevance set was verified.
using AiRaccoon.Benchmarks.Corpus;

namespace AiRaccoon.Benchmarks.Corpus;

/// <summary>Real-world queries with honest ground-truth relevance judgments.</summary>
public static class RealWorldQueries
{
    public static IReadOnlyList<CorpusQuery> Queries { get; } =
    [
        // judgment: query restates the decision/heading of ai-badger-agents-architect; same-topic docs keyword-verified
        new("doc-ai-badger-agents-architect", """
What does the project decide or document about: Architect?
""", ["ai-badger-agents-architect"]),
        // judgment: query restates the decision/heading of ai-badger-agents-dotnet-engineer; same-topic docs keyword-verified
        new("doc-ai-badger-agents-dotnet-engineer", """
What does the project decide or document about: NET Engineer?
""", ["ai-badger-agents-dotnet-engineer", "ai-badger-agents-test-engineer", "ai-badger-invariants-prove-the-check-fails", "ai-badger-invariants-tdd-mandatory", "ai-badger-skills-debug-issue-skill", "ai-badger-skills-dotnet-domain-modeling-skill", "ai-badger-skills-scripts-tooling-refactor-skill"]),
        // judgment: query restates the decision/heading of ai-badger-instructions-csharp-instructions; same-topic docs keyword-verified
        new("doc-ai-badger-instructions-csharp-instructions", """
What does the project decide or document about: C# and .NET?
""", ["ai-badger-instructions-csharp-instructions"]),
        // judgment: query restates the decision/heading of ai-badger-instructions-mcp-instructions; same-topic docs keyword-verified
        new("doc-ai-badger-instructions-mcp-instructions", """
What does the project decide or document about: MCP Server?
""", ["ai-badger-instructions-mcp-instructions", "ai-badger-invariants-mcp-thin", "ai-badger-skills-dotnet-mcp-server-skill", "ai-badger-skills-mcp-index-skill", "ai-badger-skills-mcp-tool-surface-testing-skill", "claude", "docs-adr-0001-fluentvalidation-in-core", "docs-adr-0035-memory-get-and-query-relevant-snippets", "docs-adr-0065-the-tool-layer-holds-no-pipeline", "docs-how-to-monitor-and-export-telemetry", "docs-how-to-read-performance-metrics", "docs-reference-agent-memory-server", "docs-reference-readme", "docs-tutorials-get-started-with-ai-raccoon", "docs-tutorials-readme", "hermes", "readme"]),
        // judgment: query restates the decision/heading of ai-badger-invariants-check-sources-not-yourself; same-topic docs keyword-verified
        new("doc-ai-badger-invariants-check-sources-not-yourself", """
What does the project decide or document about: Check the source, not your own reasoning?
""", ["ai-badger-invariants-check-sources-not-yourself"]),
        // judgment: query restates the decision/heading of ai-badger-invariants-derive-or-delete-the-list; same-topic docs keyword-verified
        new("doc-ai-badger-invariants-derive-or-delete-the-list", """
What does the project decide or document about: Derive the list, or delete it?
""", ["ai-badger-invariants-derive-or-delete-the-list"]),
        // judgment: query restates the decision/heading of ai-badger-invariants-high-performance-logging; same-topic docs keyword-verified
        new("doc-ai-badger-invariants-high-performance-logging", """
What does the project decide or document about: High-performance logging?
""", ["ai-badger-invariants-high-performance-logging", "ai-badger-skills-dotnet-hosted-service-review-skill", "ai-badger-skills-dotnet-logger-message-design-skill", "docs-reference-logging-event-ids"]),
        // judgment: query restates the decision/heading of ai-badger-invariants-minimal-comments; same-topic docs keyword-verified
        new("doc-ai-badger-invariants-minimal-comments", """
What does the project decide or document about: Minimal comments?
""", ["ai-badger-invariants-minimal-comments"]),
        // judgment: query restates the decision/heading of ai-badger-invariants-no-hardcoded-secrets; same-topic docs keyword-verified
        new("doc-ai-badger-invariants-no-hardcoded-secrets", """
What does the project decide or document about: No hardcoded secrets?
""", ["ai-badger-invariants-no-hardcoded-secrets", "ai-badger-invariants-no-hand-rolled-crypto"]),
        // judgment: query restates the decision/heading of ai-badger-invariants-plain-names; same-topic docs keyword-verified
        new("doc-ai-badger-invariants-plain-names", """
What does the project decide or document about: Plain names?
""", ["ai-badger-invariants-plain-names"]),
        // judgment: query restates the decision/heading of ai-badger-invariants-prove-the-check-fails; same-topic docs keyword-verified
        new("doc-ai-badger-invariants-prove-the-check-fails", """
What does the project decide or document about: A check you have not seen fail is not a check?
""", ["ai-badger-invariants-prove-the-check-fails", "ai-badger-agents-dotnet-engineer", "ai-badger-agents-test-engineer", "ai-badger-invariants-tdd-mandatory", "ai-badger-skills-debug-issue-skill", "ai-badger-skills-dotnet-domain-modeling-skill", "ai-badger-skills-scripts-tooling-refactor-skill"]),
        // judgment: query restates the decision/heading of ai-badger-invariants-state-transitions-through-a-machine; same-topic docs keyword-verified
        new("doc-ai-badger-invariants-state-transitions-through-a-machine", """
What does the project decide or document about: Route state transitions through a state machine?
""", ["ai-badger-invariants-state-transitions-through-a-machine", "docs-adr-0024-unknown-id-contract"]),
        // judgment: query restates the decision/heading of ai-badger-invariants-traceable-releases; same-topic docs keyword-verified
        new("doc-ai-badger-invariants-traceable-releases", """
What does the project decide or document about: Releases are traceable?
""", ["ai-badger-invariants-traceable-releases"]),
        // judgment: query restates the decision/heading of ai-badger-skills-artifact-verification-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-artifact-verification-skill", """
What does the project decide or document about: Artifact verification (non-code work products)?
""", ["ai-badger-skills-artifact-verification-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-code-review-evidence-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-code-review-evidence-skill", """
What does the project decide or document about: Code Review Evidence?
""", ["ai-badger-skills-code-review-evidence-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-create-task-spec-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-create-task-spec-skill", """
What does the project decide or document about: create-task-spec?
""", ["ai-badger-skills-create-task-spec-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-design-gate-audit-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-design-gate-audit-skill", """
What does the project decide or document about: design-gate-audit?
""", ["ai-badger-skills-design-gate-audit-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-dotnet-bdd-testing-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-dotnet-bdd-testing-skill", """
What does the project decide or document about: BDD / Gherkin testing in .NET?
""", ["ai-badger-skills-dotnet-bdd-testing-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-dotnet-hosted-service-review-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-dotnet-hosted-service-review-skill", """
What does the project decide or document about: dotnet-hosted-service-review?
""", ["ai-badger-skills-dotnet-hosted-service-review-skill", "ai-badger-invariants-high-performance-logging", "ai-badger-skills-dotnet-logger-message-design-skill", "docs-reference-logging-event-ids"]),
        // judgment: query restates the decision/heading of ai-badger-skills-dotnet-mcp-server-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-dotnet-mcp-server-skill", """
What does the project decide or document about: dotnet-mcp-server?
""", ["ai-badger-skills-dotnet-mcp-server-skill", "ai-badger-instructions-mcp-instructions", "ai-badger-invariants-mcp-thin", "ai-badger-skills-mcp-index-skill", "ai-badger-skills-mcp-tool-surface-testing-skill", "claude", "docs-adr-0001-fluentvalidation-in-core", "docs-adr-0035-memory-get-and-query-relevant-snippets", "docs-adr-0065-the-tool-layer-holds-no-pipeline", "docs-how-to-monitor-and-export-telemetry", "docs-how-to-read-performance-metrics", "docs-reference-agent-memory-server", "docs-reference-readme", "docs-tutorials-get-started-with-ai-raccoon", "docs-tutorials-readme", "hermes", "readme"]),
        // judgment: query restates the decision/heading of ai-badger-skills-dotnet-tool-publishing-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-dotnet-tool-publishing-skill", """
What does the project decide or document about: Publishing .NET CLI tools (PackAsTool - NuGet)?
""", ["ai-badger-skills-dotnet-tool-publishing-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-feed-badger-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-feed-badger-skill", """
What does the project decide or document about: feed-badger?
""", ["ai-badger-skills-feed-badger-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-mcp-index-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-mcp-index-skill", """
What does the project decide or document about: MCP Tool Index?
""", ["ai-badger-skills-mcp-index-skill", "ai-badger-instructions-mcp-instructions", "ai-badger-invariants-mcp-thin", "ai-badger-skills-dotnet-mcp-server-skill", "ai-badger-skills-mcp-tool-surface-testing-skill", "claude", "docs-adr-0001-fluentvalidation-in-core", "docs-adr-0035-memory-get-and-query-relevant-snippets", "docs-adr-0065-the-tool-layer-holds-no-pipeline", "docs-how-to-monitor-and-export-telemetry", "docs-how-to-read-performance-metrics", "docs-reference-agent-memory-server", "docs-reference-readme", "docs-tutorials-get-started-with-ai-raccoon", "docs-tutorials-readme", "hermes", "readme"]),
        // judgment: query restates the decision/heading of ai-badger-skills-multi-lane-report-assembly-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-multi-lane-report-assembly-skill", """
What does the project decide or document about: Multi-lane report assembly?
""", ["ai-badger-skills-multi-lane-report-assembly-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-pre-push-gate-debugging-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-pre-push-gate-debugging-skill", """
What does the project decide or document about: Pre-push verification gate debugging?
""", ["ai-badger-skills-pre-push-gate-debugging-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-research-record-audit-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-research-record-audit-skill", """
What does the project decide or document about: Research Record Audit?
""", ["ai-badger-skills-research-record-audit-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-scripts-tooling-refactor-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-scripts-tooling-refactor-skill", """
What does the project decide or document about: Scripts-tooling refactor?
""", ["ai-badger-skills-scripts-tooling-refactor-skill", "ai-badger-agents-dotnet-engineer", "ai-badger-agents-test-engineer", "ai-badger-invariants-prove-the-check-fails", "ai-badger-invariants-tdd-mandatory", "ai-badger-skills-debug-issue-skill", "ai-badger-skills-dotnet-domain-modeling-skill"]),
        // judgment: query restates the decision/heading of ai-badger-skills-sqlite-bank-space-diagnosis-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-sqlite-bank-space-diagnosis-skill", """
What does the project decide or document about: SQLite bank space & WAL diagnosis (physical layer)?
""", ["ai-badger-skills-sqlite-bank-space-diagnosis-skill", "docs-adr-0036-engine-aware-chunk-token-budget", "docs-adr-0084-arbitrary-embedding-models-are-manifest-described", "docs-how-to-configure-embedding-engines", "docs-reference-embedding-benchmark", "docs-reference-readme"]),
        // judgment: query restates the decision/heading of ai-badger-skills-update-documentation-skill; same-topic docs keyword-verified
        new("doc-ai-badger-skills-update-documentation-skill", """
What does the project decide or document about: Update documentation?
""", ["ai-badger-skills-update-documentation-skill"]),
        // judgment: query restates the decision/heading of claude; same-topic docs keyword-verified
        new("doc-claude", """
What does the project decide or document about: AiRaccoon?
""", ["claude", "ai-badger-instructions-mcp-instructions", "ai-badger-invariants-mcp-thin", "ai-badger-skills-dotnet-mcp-server-skill", "ai-badger-skills-mcp-index-skill", "ai-badger-skills-mcp-tool-surface-testing-skill", "docs-adr-0001-fluentvalidation-in-core", "docs-adr-0035-memory-get-and-query-relevant-snippets", "docs-adr-0065-the-tool-layer-holds-no-pipeline", "docs-how-to-monitor-and-export-telemetry", "docs-how-to-read-performance-metrics", "docs-reference-agent-memory-server", "docs-reference-readme", "docs-tutorials-get-started-with-ai-raccoon", "docs-tutorials-readme", "hermes", "readme"]),
        // judgment: query restates the decision/heading of docs-adr-0003-source-file-first-class-citizen; same-topic docs keyword-verified
        new("doc-docs-adr-0003-source-file-first-class-citizen", """
What does the project decide or document about: 0003 — Source file as first-class citizen (source_file schema + weighted FTS)?
""", ["docs-adr-0003-source-file-first-class-citizen"]),
        // judgment: query restates the decision/heading of docs-adr-0006-rrf-parameter-optimization; same-topic docs keyword-verified
        new("doc-docs-adr-0006-rrf-parameter-optimization", """
What does the project decide or document about: 0006 — RRF parameter optimization: k, weight ratio, minScore, candidate window?
""", ["docs-adr-0006-rrf-parameter-optimization"]),
        // judgment: query restates the decision/heading of docs-adr-0009-otlp-export; same-topic docs keyword-verified
        new("doc-docs-adr-0009-otlp-export", """
What does the project decide or document about: 0009 — OTLP export?
""", ["docs-adr-0009-otlp-export"]),
        // judgment: query restates the decision/heading of docs-adr-0012-ssh-key-derivation-hkdf-replacement; same-topic docs keyword-verified
        new("doc-docs-adr-0012-ssh-key-derivation-hkdf-replacement", """
What does the project decide or document about: 0012 — Replace the hand-rolled SSH-key derivation with platform HKDF?
""", ["docs-adr-0012-ssh-key-derivation-hkdf-replacement"]),
        // judgment: query restates the decision/heading of docs-adr-0015-retrieval-gates-assert-portable-bands; same-topic docs keyword-verified
        new("doc-docs-adr-0015-retrieval-gates-assert-portable-bands", """
What does the project decide or document about: 0015 — Retrieval gates assert portable bands, not machine-exact pins?
""", ["docs-adr-0015-retrieval-gates-assert-portable-bands"]),
        // judgment: query restates the decision/heading of docs-adr-0018-promotion-scoring-v2; same-topic docs keyword-verified
        new("doc-docs-adr-0018-promotion-scoring-v2", """
What does the project decide or document about: 0018 — Promotion scoring v2: archetype prior + content evidence?
""", ["docs-adr-0018-promotion-scoring-v2"]),
        // judgment: query restates the decision/heading of docs-adr-0021-export-the-aspnet-request-span; same-topic docs keyword-verified
        new("doc-docs-adr-0021-export-the-aspnet-request-span", """
What does the project decide or document about: 0021 — Export the ASP.NET request span?
""", ["docs-adr-0021-export-the-aspnet-request-span"]),
        // judgment: query restates the decision/heading of docs-adr-0024-unknown-id-contract; same-topic docs keyword-verified
        new("doc-docs-adr-0024-unknown-id-contract", """
What does the project decide or document about: 0024 — Unknown ids: idempotent removal reports a count, a state transition refuses?
""", ["docs-adr-0024-unknown-id-contract", "ai-badger-invariants-state-transitions-through-a-machine"]),
        // judgment: query restates the decision/heading of docs-adr-0027-extensible-file-type-handlers-and-json-support; same-topic docs keyword-verified
        new("doc-docs-adr-0027-extensible-file-type-handlers-and-json-support", """
What does the project decide or document about: 0027 — Extensible FileType Handlers and JSON Support?
""", ["docs-adr-0027-extensible-file-type-handlers-and-json-support"]),
        // judgment: query restates the decision/heading of docs-adr-0031-polly-resilience-pipelines; same-topic docs keyword-verified
        new("doc-docs-adr-0031-polly-resilience-pipelines", """
What does the project decide or document about: 0031. Polly Resilience Pipelines with Exponential Backoff and Decorrelated Jitter?
""", ["docs-adr-0031-polly-resilience-pipelines"]),
        // judgment: query restates the decision/heading of docs-adr-0034-explicit-ttl-is-authoritative; same-topic docs keyword-verified
        new("doc-docs-adr-0034-explicit-ttl-is-authoritative", """
What does the project decide or document about: 0034. An Explicit TTL Is Authoritative?
""", ["docs-adr-0034-explicit-ttl-is-authoritative"]),
        // judgment: query restates the decision/heading of docs-adr-0037-workspace-and-promotion-queue-concurrency-guards; same-topic docs keyword-verified
        new("doc-docs-adr-0037-workspace-and-promotion-queue-concurrency-guards", """
What does the project decide or document about: 0037. Workspace and Promotion-Queue Concurrency Guards?
""", ["docs-adr-0037-workspace-and-promotion-queue-concurrency-guards"]),
        // judgment: query restates the decision/heading of docs-adr-0040-read-path-query-guard; same-topic docs keyword-verified
        new("doc-docs-adr-0040-read-path-query-guard", """
What does the project decide or document about: 0040. Read-Path Query Guard?
""", ["docs-adr-0040-read-path-query-guard"]),
        // judgment: query restates the decision/heading of docs-adr-0044-section-fts-weight; same-topic docs keyword-verified
        new("doc-docs-adr-0044-section-fts-weight", """
What does the project decide or document about: 0044. The section column's FTS weight is 4, not 16?
""", ["docs-adr-0044-section-fts-weight", "ai-badger-skills-sqlite-bank-space-diagnosis-skill", "docs-adr-0068-ctx-is-a-vec0-metadata-column-not-a-partition-key", "docs-adr-0070-maintenance-is-a-list-of-jobs-with-a-ledger", "readme"]),
        // judgment: query restates the decision/heading of docs-adr-0048-a-chunk-is-a-well-formed-markdown-fragment; same-topic docs keyword-verified
        new("doc-docs-adr-0048-a-chunk-is-a-well-formed-markdown-fragment", """
What does the project decide or document about: 0048. A chunk is a well-formed markdown fragment?
""", ["docs-adr-0048-a-chunk-is-a-well-formed-markdown-fragment"]),
        // judgment: query restates the decision/heading of docs-adr-0052-the-workspace-lifecycle-is-a-write-not-a-destruction; same-topic docs keyword-verified
        new("doc-docs-adr-0052-the-workspace-lifecycle-is-a-write-not-a-destruction", """
What does the project decide or document about: 0052. The workspace lifecycle is a write, not a destruction?
""", ["docs-adr-0052-the-workspace-lifecycle-is-a-write-not-a-destruction"]),
        // judgment: query restates the decision/heading of docs-adr-0055-a-discard-is-load-bearing-while-its-entry-lives; same-topic docs keyword-verified
        new("doc-docs-adr-0055-a-discard-is-load-bearing-while-its-entry-lives", """
What does the project decide or document about: 0055. A discard is load-bearing while its entry lives?
""", ["docs-adr-0055-a-discard-is-load-bearing-while-its-entry-lives"]),
        // judgment: query restates the decision/heading of docs-adr-0059-the-layering-guard-the-repo-had-already-paid-for; same-topic docs keyword-verified
        new("doc-docs-adr-0059-the-layering-guard-the-repo-had-already-paid-for", """
What does the project decide or document about: 0059. The layering guard the repo had already paid for?
""", ["docs-adr-0059-the-layering-guard-the-repo-had-already-paid-for"]),
        // judgment: query restates the decision/heading of docs-adr-0062-a-fake-clock-advanced-before-its-timer-exists-is-lost; same-topic docs keyword-verified
        new("doc-docs-adr-0062-a-fake-clock-advanced-before-its-timer-exists-is-lost", """
What does the project decide or document about: 0062. A fake clock advanced before its timer exists is lost?
""", ["docs-adr-0062-a-fake-clock-advanced-before-its-timer-exists-is-lost"]),
        // judgment: query restates the decision/heading of docs-adr-0065-the-tool-layer-holds-no-pipeline; same-topic docs keyword-verified
        new("doc-docs-adr-0065-the-tool-layer-holds-no-pipeline", """
What does the project decide or document about: 0065. The tool layer holds no pipeline?
""", ["docs-adr-0065-the-tool-layer-holds-no-pipeline", "ai-badger-instructions-mcp-instructions", "ai-badger-invariants-mcp-thin", "ai-badger-skills-dotnet-mcp-server-skill", "ai-badger-skills-mcp-index-skill", "ai-badger-skills-mcp-tool-surface-testing-skill", "claude", "docs-adr-0001-fluentvalidation-in-core", "docs-adr-0035-memory-get-and-query-relevant-snippets", "docs-how-to-monitor-and-export-telemetry", "docs-how-to-read-performance-metrics", "docs-reference-agent-memory-server", "docs-reference-readme", "docs-tutorials-get-started-with-ai-raccoon", "docs-tutorials-readme", "hermes", "readme"]),
        // judgment: query restates the decision/heading of docs-adr-0068-ctx-is-a-vec0-metadata-column-not-a-partition-key; same-topic docs keyword-verified
        new("doc-docs-adr-0068-ctx-is-a-vec0-metadata-column-not-a-partition-key", """
What does the project decide or document about: 0068. ctx is a vec0 metadata column, not a partition key?
""", ["docs-adr-0068-ctx-is-a-vec0-metadata-column-not-a-partition-key", "ai-badger-skills-sqlite-bank-space-diagnosis-skill", "docs-adr-0044-section-fts-weight", "docs-adr-0070-maintenance-is-a-list-of-jobs-with-a-ledger", "readme"]),
        // judgment: query restates the decision/heading of docs-adr-0071-a-query-is-trimmed-deliberately-and-said-so; same-topic docs keyword-verified
        new("doc-docs-adr-0071-a-query-is-trimmed-deliberately-and-said-so", """
What does the project decide or document about: 0071. A query is trimmed deliberately, and says so?
""", ["docs-adr-0071-a-query-is-trimmed-deliberately-and-said-so"]),
        // judgment: query restates the decision/heading of docs-adr-0074-a-capped-buffer-satisfies-the-channel-rule-and-reshapes-; same-topic docs keyword-verified
        new("doc-docs-adr-0074-a-capped-buffer-satisfies-the-channel-rule-and-reshapes-", """
What does the project decide or document about: 0074. A capped buffer satisfies the channel rule, and reshapes G4?
""", ["docs-adr-0074-a-capped-buffer-satisfies-the-channel-rule-and-reshapes-"]),
        // judgment: query restates the decision/heading of docs-adr-0077-table-chunking-is-not-adjudicable-on-a-table-blind-corpu; same-topic docs keyword-verified
        new("doc-docs-adr-0077-table-chunking-is-not-adjudicable-on-a-table-blind-corpu", """
What does the project decide or document about: 0077. Table chunking is not adjudicable on a table-blind corpus, and does not ship?
""", ["docs-adr-0077-table-chunking-is-not-adjudicable-on-a-table-blind-corpu"]),
        // judgment: query restates the decision/heading of docs-adr-0080-the-phases-close-against-search-total-not-the-tool-total; same-topic docs keyword-verified
        new("doc-docs-adr-0080-the-phases-close-against-search-total-not-the-tool-total", """
What does the project decide or document about: 0080. The phases close against search.total, not the tool total?
""", ["docs-adr-0080-the-phases-close-against-search-total-not-the-tool-total"]),
        // judgment: query restates the decision/heading of docs-adr-0083-search-parameters-unified-source; same-topic docs keyword-verified
        new("doc-docs-adr-0083-search-parameters-unified-source", """
What does the project decide or document about: 0083. SearchParameters — one resolved record for every search?
""", ["docs-adr-0083-search-parameters-unified-source"]),
        // judgment: query restates the decision/heading of docs-adr-0086-watch-overlap-and-ai-raccoon-ignore; same-topic docs keyword-verified
        new("doc-docs-adr-0086-watch-overlap-and-ai-raccoon-ignore", """
What does the project decide or document about: 0086. Watch overlap resolution and ai-raccoon.ignore — one-transaction prune/reject, no version bump?
""", ["docs-adr-0086-watch-overlap-and-ai-raccoon-ignore"]),
        // judgment: query restates the decision/heading of docs-adr-0089-the-project-id-is-a-guidv7-and-that-is-not-access-contro; same-topic docs keyword-verified
        new("doc-docs-adr-0089-the-project-id-is-a-guidv7-and-that-is-not-access-contro", """
What does the project decide or document about: 0089. The project id is a registered guidv7 — accident prevention, not access control?
""", ["docs-adr-0089-the-project-id-is-a-guidv7-and-that-is-not-access-contro"]),
        // judgment: query restates the decision/heading of docs-explanation-agent-memory-capabilities; same-topic docs keyword-verified
        new("doc-docs-explanation-agent-memory-capabilities", """
What does the project decide or document about: Agent memory capabilities and tiered lifecycle?
""", ["docs-explanation-agent-memory-capabilities", "claude", "hermes"]),
        // judgment: query restates the decision/heading of docs-explanation-readme; same-topic docs keyword-verified
        new("doc-docs-explanation-readme", """
What does the project decide or document about: explanation/?
""", ["docs-explanation-readme"]),
        // judgment: query restates the decision/heading of docs-how-to-configure-rider-local-autocompletion; same-topic docs keyword-verified
        new("doc-docs-how-to-configure-rider-local-autocompletion", """
What does the project decide or document about: Configure Rider AI completion with a local Qwen3.5-9B?
""", ["docs-how-to-configure-rider-local-autocompletion"]),
        // judgment: query restates the decision/heading of docs-how-to-readme; same-topic docs keyword-verified
        new("doc-docs-how-to-readme", """
What does the project decide or document about: how-to/?
""", ["docs-how-to-readme"]),
        // judgment: query restates the decision/heading of docs-reference-agent-memory-server; same-topic docs keyword-verified
        new("doc-docs-reference-agent-memory-server", """
What does the project decide or document about: Agent memory server — reference?
""", ["docs-reference-agent-memory-server", "ai-badger-instructions-mcp-instructions", "ai-badger-invariants-mcp-thin", "ai-badger-skills-dotnet-mcp-server-skill", "ai-badger-skills-mcp-index-skill", "ai-badger-skills-mcp-tool-surface-testing-skill", "claude", "docs-adr-0001-fluentvalidation-in-core", "docs-adr-0035-memory-get-and-query-relevant-snippets", "docs-adr-0065-the-tool-layer-holds-no-pipeline", "docs-how-to-monitor-and-export-telemetry", "docs-how-to-read-performance-metrics", "docs-reference-readme", "docs-tutorials-get-started-with-ai-raccoon", "docs-tutorials-readme", "hermes", "readme"]),
        // judgment: query restates the decision/heading of docs-reference-readme; same-topic docs keyword-verified
        new("doc-docs-reference-readme", """
What does the project decide or document about: reference/?
""", ["docs-reference-readme", "ai-badger-skills-sqlite-bank-space-diagnosis-skill", "docs-adr-0036-engine-aware-chunk-token-budget", "docs-adr-0084-arbitrary-embedding-models-are-manifest-described", "docs-how-to-configure-embedding-engines", "docs-reference-embedding-benchmark"]),
        // judgment: query restates the decision/heading of docs-tutorials-readme; same-topic docs keyword-verified
        new("doc-docs-tutorials-readme", """
What does the project decide or document about: tutorials/?
""", ["docs-tutorials-readme", "ai-badger-instructions-mcp-instructions", "ai-badger-invariants-mcp-thin", "ai-badger-skills-dotnet-mcp-server-skill", "ai-badger-skills-mcp-index-skill", "ai-badger-skills-mcp-tool-surface-testing-skill", "claude", "docs-adr-0001-fluentvalidation-in-core", "docs-adr-0035-memory-get-and-query-relevant-snippets", "docs-adr-0065-the-tool-layer-holds-no-pipeline", "docs-how-to-monitor-and-export-telemetry", "docs-how-to-read-performance-metrics", "docs-reference-agent-memory-server", "docs-reference-readme", "docs-tutorials-get-started-with-ai-raccoon", "hermes", "readme"]),
        // judgment: all docs whose title/body covers 'mcp' were keyword-verified
        new("cluster-mcp", """
How does this project expose tools to AI assistants over the Model Context Protocol?
""", ["ai-badger-instructions-mcp-instructions", "ai-badger-invariants-mcp-thin", "ai-badger-skills-dotnet-mcp-server-skill", "ai-badger-skills-mcp-index-skill", "ai-badger-skills-mcp-tool-surface-testing-skill", "claude", "docs-adr-0001-fluentvalidation-in-core", "docs-adr-0035-memory-get-and-query-relevant-snippets", "docs-adr-0065-the-tool-layer-holds-no-pipeline", "docs-how-to-monitor-and-export-telemetry", "docs-how-to-read-performance-metrics", "docs-reference-agent-memory-server", "docs-reference-readme", "docs-tutorials-get-started-with-ai-raccoon", "docs-tutorials-readme", "hermes", "readme"]),
        // judgment: all docs whose title/body covers 'tdd' were keyword-verified
        new("cluster-tdd", """
What rule governs when production code may be written relative to tests?
""", ["ai-badger-agents-dotnet-engineer", "ai-badger-agents-test-engineer", "ai-badger-invariants-prove-the-check-fails", "ai-badger-invariants-tdd-mandatory", "ai-badger-skills-debug-issue-skill", "ai-badger-skills-dotnet-domain-modeling-skill", "ai-badger-skills-scripts-tooling-refactor-skill"]),
        // judgment: all docs whose title/body covers 'validation' were keyword-verified
        new("cluster-validation", """
Which library keeps domain validation rules colocated with the models?
""", ["ai-badger-skills-dotnet-domain-modeling-skill", "docs-adr-0001-fluentvalidation-in-core", "docs-adr-readme"]),
        // judgment: all docs whose title/body covers 'vector-search' were keyword-verified
        new("cluster-vector-search", """
How does the fused retriever combine keyword search and vector search results?
""", ["ai-badger-skills-sqlite-bank-space-diagnosis-skill", "docs-adr-0044-section-fts-weight", "docs-adr-0068-ctx-is-a-vec0-metadata-column-not-a-partition-key", "docs-adr-0070-maintenance-is-a-list-of-jobs-with-a-ledger", "readme"]),
        // judgment: all docs whose title/body covers 'embedding-model' were keyword-verified
        new("cluster-embedding-model", """
What embedding model format and pooling strategy does the project use?
""", ["ai-badger-skills-sqlite-bank-space-diagnosis-skill", "docs-adr-0036-engine-aware-chunk-token-budget", "docs-adr-0084-arbitrary-embedding-models-are-manifest-described", "docs-how-to-configure-embedding-engines", "docs-reference-embedding-benchmark", "docs-reference-readme"]),
        // judgment: all docs whose title/body covers 'identity' were keyword-verified
        new("cluster-identity", """
How does the project mint identifiers that are both unique and sortable by time?
""", ["docs-adr-0089-the-project-id-is-a-guidv7-and-that-is-not-access-contro"]),
        // judgment: all docs whose title/body covers 'logging' were keyword-verified
        new("cluster-logging", """
What convention wraps a high-performance logging call?
""", ["ai-badger-invariants-high-performance-logging", "ai-badger-skills-dotnet-hosted-service-review-skill", "ai-badger-skills-dotnet-logger-message-design-skill", "docs-reference-logging-event-ids"]),
        // judgment: all docs whose title/body covers 'workspace' were keyword-verified
        new("cluster-workspace", """
How does an in-progress workspace stay isolated from committed project memory?
""", ["claude", "docs-explanation-agent-memory-capabilities", "hermes"]),
        // judgment: all docs whose title/body covers 'promotion' were keyword-verified
        new("cluster-promotion", """
How does content move from a workspace into the shared promotion tier?
""", ["claude", "hermes"]),
        // judgment: all docs whose title/body covers 'security' were keyword-verified
        new("cluster-security", """
How are secrets and credentials kept out of tracked files?
""", ["ai-badger-invariants-no-hand-rolled-crypto", "ai-badger-invariants-no-hardcoded-secrets"]),
        // judgment: all docs whose title/body covers 'state-machine' were keyword-verified
        new("cluster-state-machine", """
How are a domain object's state transitions constrained to the declared ones?
""", ["ai-badger-invariants-state-transitions-through-a-machine", "docs-adr-0024-unknown-id-contract"]),
        // judgment: all docs whose title/body covers 'guard-clauses' were keyword-verified
        new("cluster-guard-clauses", """
What replaces a hand-rolled null check for argument validation?
""", ["ai-badger-invariants-guard-clauses"]),
    ];
}
