---
name: get-raw-grep-result
description: >-
  Use when the memory-first gate returned a pattern hash and you need the short-lived raw
  output from its shell grep or rg capture.
version: 0.1.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [grep, rg, search, retrieval]
    related_skills: [ai-raccoon-memory]
---

# Get Raw Grep Result

Use this skill when a memory-first gate denial provides a `pattern-hash` and the original
grep or `rg` output is needed.

## Procedure

1. Read the `pattern-hash` from the gate's denial reason. The handle is a SHA-256 digest of
   the exact pattern text.
2. Run `python3 <path-to-this-skill>/scripts/get_raw_grep_result.py --pattern-hash <hash>`.
   For an exact pattern string, use `--pattern <pattern>` instead.
3. Parse the JSON response. `status` is `pending`, `ready`, `expired`, or `error`; a ready
   result contains `stdout`, `stderr`, and `returncode`.
4. If the status is `pending`, retry promptly. Results expire about 120 seconds after capture
   begins and are removed when fetched after expiry. Run the command from the same working
   directory as the original search; results are isolated by working directory.

## Pitfalls

- Pass exactly one of `--pattern` and `--pattern-hash`.
- Preserve the pattern exactly when using `--pattern`; even whitespace changes its hash.
- A grep exit code of 1 means no matches and is still a completed `ready` result.
- Each stdout and stderr stream is limited to 1 MiB; the JSON marks truncated streams.
- The capture is best-effort and only starts for shell grep-family commands on a first-sighting
  gate denial. It is not a durable search history.

## Gotchas

No environment-specific gotchas known.

## Verification

Confirm the output is valid JSON and the returned `hash` matches the requested handle. For a
`ready` response, inspect `returncode`, `stdout`, and `stderr` before relying on the result.
