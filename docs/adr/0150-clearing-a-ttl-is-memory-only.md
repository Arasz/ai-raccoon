# ADR-0150: Clearing a TTL is Memory-only, like setting one

Date: 2026-11-13

Status: Accepted for 2.9.1.

Extends: [ADR-0148](0148-bank-guards-memory-only-ttl-and-the-kind-domain.md)

## Context

2.1.0 made `set_memory_ttl` refuse both setting and clearing a TTL on a non-Memory entry. 2.2.0 opened clearing to every kind again, because the sweep still honoured a TTL that a File row stored before 2.1.0. Without the clear, such a TTL could not be removed or stopped.

ADR-0148 removed that reason. Schema v24 clears the TTL of every non-Memory row, and its triggers refuse a new one. On a bank that runs 2.9.0, no non-Memory row can carry a TTL. A clear on a File or unclassified entry is now a no-op that reports success.

The 2.9.0 manual checklist (`typed-memory-only-ttl`) failed on this: the check and the 2.1.0 changelog say clearing is refused, and the product accepted it.

## Decision

**`set_memory_ttl` refuses clearing on a non-Memory entry, the same as setting.** `UpdateEntryTtl` matches only `entry_type = 'memory'`, for a value and for `null`. A hash the project holds as another kind is refused as `not-a-memory-entry`. A hash the project does not hold stays `unknown-hash`.

## Alternatives

- **Keep the no-op clear and fix the docs.** Rejected. A success reply for a request that cannot do anything tells the caller the entry had a TTL to clear. The refusal names the real state.

## Consequences

- **Positive.** Setting and clearing share one rule, and the SQL, the tool description and the docs say the same thing.
- **Negative.** A caller that cleared a TTL on a File entry now gets `not-a-memory-entry` instead of success. The bank does not change, because that TTL cannot exist.
