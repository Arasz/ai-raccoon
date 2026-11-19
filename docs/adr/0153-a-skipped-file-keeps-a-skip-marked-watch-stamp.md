# ADR-0153: A skipped file keeps a skip-marked watch stamp

Date: 2026-11-19

Status: Accepted for 2.10.2.

Amends: [0121](0121-restart-catch-up-compares-each-file-with-its-own-fingerprint.md) (what counts as a fingerprint).

## Context

A routed file (for example `.md` or `.ipynb`) whose content ingest refuses (binary bytes, unparseable content, or code
that gives zero chunks) returns `FingerprintEligible = false`. The replace then deleted the file's `watch_files` row.
Catch-up (`WatchCatchUp.IsDue`) treats a file with no row as due, so every reconcile pass read, hashed and replaced
the file again. Each pass committed a transaction and logged EventId 899 at Information with `0 row(s) written`.
An owner saw this line continuously on a Windows host running 2.10.1.

The row was deleted on purpose: a stored real hash would hash-skip the file for good, also after a better chunker
arrives, and the old hash must not outlive the content it described.

## Decision

1. **A skipped replace upserts the row with `file_hash = 'skip:' + hash`** instead of deleting it. `updated_at` is the
   time of the replace, as for an indexed file. `file_hash` stays `NOT NULL`; no schema change.
2. **Catch-up reads the row as a normal stamp.** An unchanged file is not due. A change of its mtime or size makes
   it due again.
3. **A digest still replaces the file.** The marker equals no SHA-256 hex digest, so the hash-skip check and the
   replace guard never match it. A changed file, and a full scan, retry the ingest.

## Consequences

- An unchanged skipped file costs nothing per pass. EventId 899 logs once per real change of the file.
- Reconcile, ignore and remove paths delete a skip row the same way as a real fingerprint.
- A file a stand-in code chunker refused is retried only when it changes or a full scan runs; a changed-only
  restart catch-up does not retry it.
- A file no corpus routes (`SkipUnroutedAsync`) is unchanged: it never reaches the replace and keeps no row.
