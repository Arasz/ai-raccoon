# ADR-0154: The ignore file keeps a real fingerprint, and the tokenizer reads a lone surrogate as U+FFFD

Date: 2026-11-19

Status: Accepted for 2.10.3.

Amends: [0153](0153-a-skipped-file-keeps-a-skip-marked-watch-stamp.md) (the ignore file is not a skipped file).

## Context

**Rescan loop.** A digest of a watch root's `ai-raccoon.ignore` bypasses the routing check and calls
replace-by-path. No corpus takes the file, so ingest returns `Skipped`. Before 2.10.2 the replace stored no
fingerprint; since 2.10.2 it stores the `skip:` marker, which by design equals no real hash. Either way the
next digest of the ignore file runs the replace again. After each replace the digest queues a full rescan, and
the full rescan digests the ignore file again. A watch whose root holds an ignore file therefore walked its tree
about every 1.5 s on an unchanged folder. Each pass held the write lock for a 0-row replace. Watches without a
root ignore file did not loop.

**Invalid text.** A file whose decoded text holds a lone surrogate reaches the bundled tokenizer's
`string.Normalize`, which throws `ArgumentException` ("String contains invalid Unicode code points"). The watch
pipeline caught it as a digest failure: the whole watch went to `retrying`, and `lastError` held only the
message, so the file could not be found.

## Decision

1. **The ignore file never goes through replace-by-path.** The digest compares the file's real hash with its
   stored fingerprint. Equal: a touch, no rescan. Different: it stores the real hash and queues one full rescan.
   The ignore file is a control file, not content, so it has no chunks to replace.
2. **The bundled tokenizer reads an unpaired surrogate as U+FFFD** before it normalizes the text. This is the
   replacement UTF-8 encoding makes when the chunk is stored, so the token count matches the stored text. The
   file is indexed and the watch stays healthy. A query that holds an unpaired surrogate no longer throws either.
   Rejected: skipping the file as `unparseable`. One escape in a property name would then hide the whole file.
3. **A digest failure names its file.** `lastError` is `<path>: <message>`, and the failure is logged with the
   exception (EventId 304).

## Consequences

- An unchanged ignore file costs one hash per digest and starts no scan. An edit starts exactly one scan.
- A fingerprint row written by an older build (none, or `skip:`) differs from the real hash, so the first digest
  after the upgrade queues one more rescan and then stores the real hash.
- Content that decodes to an unpaired surrogate (a JSON property name or a notebook cell holding `\uD83D`) is indexed with U+FFFD in its place.
