# ADR-0147: `get_entry` reports the corpus in `corpus`, and its output `entryType` is deprecated

Date: 2026-11-12

Status: Accepted for 2.9.0.

## Context

`get_entry` takes an input `entryType` that names the kind to read: `memory`, `file` or `code`. Its output also has a field called `entryType`, but that field names the corpus that owns the hash (`memory` or `code`), not the kind. A `file` row therefore returns `entryType: "memory"`. One name carries two meanings, one on each side of the call, and a client that echoes the output value back as input reads a `file` row as `memory`.

The kind has its own output field already: `entryKind` (`memory`, `file` or `code`, shipped in 2.2.0). Issue #917 item 4 asked for the confusion to end. Owner ruling D3 chose an additive fix.

## Decision

- `get_entry` output gains `corpus`: `memory` or `code`. It holds the value the output `entryType` holds today.
- The output `entryType` keeps its value and is marked deprecated in the schema description: same as `corpus`; removed in 3.0.0; use `entryKind` for the kind. No client breaks in this release.
- Removal of the output `entryType` is tracked in issue #947 and lands in 3.0.0.
- The name `entryType` is never reused for the kind on the output side. A silent change of meaning would break a client that reads the old value without any error. After the removal the output has `corpus` and `entryKind`; the input parameter `entryType` is unchanged.

**Rejected alternatives.**

- *Make the output `entryType` carry the kind now.* A silent meaning change: `file` rows would flip from `memory` to `file` under the same name.
- *Remove the output `entryType` now.* A breaking change outside a major version.
- *`[Obsolete]` on the record property.* It would warn at every build under warnings-as-errors and does not reach the JSON schema. The schema description does.

## Consequences

- **Positive.** The corpus has an unambiguous name. Clients can move off `entryType` before 3.0.0.
- **Neutral.** Each `get_entry` response carries one more short string.
- **Negative.** Until 3.0.0 the output has two fields with the same value.

## Verification

`GetEntryCodeToolTests` — the serialized envelope carries `corpus` `memory` for a memory row and `file` row, and `code` for a code row, independent of `entryKind`; the output `entryType` equals `corpus`.
