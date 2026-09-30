# Re-chunk measurement setup: root A

Date: 2026-09-30. Task: `air-254-vs-1022-retrieval-measurement-809` (issue #809), step `copy-prepare`.

Data root: `/tmp/aira-rechunk-measure`

Root A is a disposable measurement copy of the pinned eval bank. The clone came from
`/Users/arasz/ai-raccoon-eval/copies/cb99fe6ecbe0.db`. The source copy and the live data root
(`~/.ai-raccoon`) were not modified, with one sidecar caveat disclosed at the end.

## Clone and identity

Command (exit 0):

```text
cp -c /Users/arasz/ai-raccoon-eval/copies/cb99fe6ecbe0.db /tmp/aira-rechunk-measure/memory.db
cp_c_exit=0
```

Size: 848,736,256 bytes. SHA-256 (clone and source, computed detached after cloning, before any
write to the clone):

```text
cb99fe6ecbe0e73b209c83c66827e26e00e0e06719dbab6dab78d44cbc91c790  memory.db
cb99fe6ecbe0e73b209c83c66827e26e00e0e06719dbab6dab78d44cbc91c790  /Users/arasz/ai-raccoon-eval/copies/cb99fe6ecbe0.db
```

Pasted stat output from the pristine clone (`clone-stat.txt`), plus the final stat after the
sidecar initialization described below. The main file never changed.

```text
clone:  memory.db bytes=848736256 mode=-rw-r--r-- inode=529573936 mtime=Sep 27 13:13:54 2026 ctime=Sep 30 10:38:38 2026
source: /Users/arasz/ai-raccoon-eval/copies/cb99fe6ecbe0.db bytes=848736256 mode=-rw-r--r-- inode=507866451 mtime=Sep 27 13:13:54 2026 ctime=Sep 27 14:04:20 2026
final:  memory.db bytes=848736256 mode=-rw-r--r-- inode=529573936 mtime=Sep 27 13:13:54 2026 ctime=Sep 30 10:38:38 2026
```

`cp -c` is not taken on faith. A second clone of the same 848 MB source consumed 12 KB of free
space, which is block sharing rather than a byte copy. A real copy would have consumed about
829,000 KB.

```text
free_kb_before=112016248 free_kb_after=112016236 delta_kb=12
```

## Asserted pinned metadata

The values are asserted with shell comparisons, not printed and assumed. The assertion script
lives at `/tmp/aira-rechunk-measure/assert-clone.sh`. Its self-test used a deliberately wrong
expectation (entries 1) and went red, so the harness can fail.

```text
--- self-test (deliberately wrong expectation: must go RED) ---
FAIL AC1 entries: expected [1] got [70763]
PASS AC1 user_version: expected [17] got [17]
PASS AC2 embedding.device: expected [coreml] got [coreml]
PASS AC2 embedding.threads: expected [3] got [3]
PASS AC3 chunkBudget count: expected [0] got [0]
selftest_exit=1
--- real assertions (must go GREEN) ---
PASS AC1 entries: expected [70763] got [70763]
PASS AC1 user_version: expected [17] got [17]
PASS AC2 embedding.device: expected [coreml] got [coreml]
PASS AC2 embedding.threads: expected [3] got [3]
PASS AC3 chunkBudget count: expected [0] got [0]
assert_exit=0
```

The exact acceptance-command outputs:

```text
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' "SELECT COUNT(*) FROM entries;"
70763
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' "PRAGMA user_version;"
17
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' "SELECT key,value FROM settings WHERE key LIKE 'embedding.%';"
embedding.provider|local
embedding.engine|local:bundled#777268daa83ea925bfbfe4e020e62d0232be6df48189280a117a3de65bbd4ff6
embedding.codeModel|bundled
embedding.codeEngine|local:bundled#777268daa83ea925bfbfe4e020e62d0232be6df48189280a117a3de65bbd4ff6
embedding.codeDimensions|384
embedding.threads|3
embedding.device|coreml
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' "SELECT COUNT(*) FROM settings WHERE key LIKE 'embedding.chunkBudget%';"
0
```

## Clone provenance

Documented provenance, with sources:

- [`docs/work/2026-09-24-harness-granite-rebaseline.md`](2026-09-24-harness-granite-rebaseline.md) line 106: the old pinned copy (`e0434a72`, `/tmp/p1-live-copy.db`) no longer existed, so `project-corpus-100.json` was re-pinned on 2026-09-27 to a fresh copy (`cb99fe6e`, 70,763 entries); "The copy now lives at `~/ai-raccoon-eval/copies/cb99fe6ecbe0.db` (the owner's `AI_RACCOON_EVAL_COPY`)."
- [`docs/work/2026-09-27-harness-granite-golden-report.md`](2026-09-27-harness-granite-golden-report.md) line 6: "bank copy /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-harness-granite-golden-regen-726/.harness-run/memory-copy.db (sha256 cb99fe6ecbe0...)" and "Bank copy entries: 70763."
- `.ai-badger/state.json` line 85: "#726 closed. Fresh live-bank copy cb99fe6e (70,763 entries) kept at ~/ai-raccoon-eval/copies/cb99fe6ecbe0.db (owner's `AI_RACCOON_EVAL_COPY`)."

So the copy was taken for the granite golden re-run of #726 on 2026-09-27 and was kept as the
owner's eval copy. HYPOTHESIS, unverified: the exact capture instant and the exact tool
invocation. The file mtime (2026-09-27 13:13:54) agrees with the report date, and the rebaseline
note names `make_memory_copy.py` for the run's copy, but no document states that this specific
file was produced by that command at that time. HYPOTHESIS, unverified: that the source was
quiesced and checkpointed before the clone. The 0-byte source WAL is consistent with a clean
close, but that is inference from the sidecars, not a documented fact.

Observation, not a defect: the clone's `embedding.engine` hash (`777268da...`) differs from the
live bank's current row (`ef600cb9...`), and the live bank carries
`embedding.chunkBudget.retryAttempts|1022:2` while the clone carries no `chunkBudget` keys. The
clone is expected to carry its own pinned rows; the measurement must be read against the clone's
settings, not the live bank's.

## Settings writes and deletions

None. Both required pins were already present on the source and therefore on the clone, so the
"write if missing" branch did not fire.

```text
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' "SELECT value FROM settings WHERE key='embedding.device';"
coreml
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' "SELECT value FROM settings WHERE key='embedding.threads';"
3
```

Zero `embedding.chunkBudget%` rows existed, so nothing was deleted. The clone keeps the
pre-config-D fingerprint; the full-migration trigger is expected downstream.

## coreml-cache symlink

```text
ln -s /Users/arasz/.ai-raccoon/coreml-cache /tmp/aira-rechunk-measure/coreml-cache
test -L /tmp/aira-rechunk-measure/coreml-cache   # PASS
readlink /tmp/aira-rechunk-measure/coreml-cache
/Users/arasz/.ai-raccoon/coreml-cache
```

The target name is the product's own: `EmbeddingService.cs` declares
`CoreMlCacheDirectoryName = "coreml-cache"` (src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:57).

Rationale: the live cache is warm. It holds a compiled CoreML model under
`e6d04965a67d/1.30.0/CPUAndNeuralEngine` (6.3 GB total), and `status.json` records
`state: NeuralEngineServing`, "4 buckets loaded; every probe row reached cosine 0.999" as of
2026-09-29. Reusing it avoids paying a cold CoreML compilation inside the measurement.

Caveat: the symlink makes the disposable root share the live cache in the write direction. The
product may write new cache entries into `~/.ai-raccoon/coreml-cache` during the measurement.
Those writes are cache-only, not bank data, but they do touch the live data root's cache
directory.

## WAL sidecar caveat for read-only opens

A clone of the `.db` alone has no `-shm`/`-wal` sidecars. This bank is WAL mode, and a read-only
connection cannot initialize the wal-index, so the exact `mode=ro` acceptance commands fail on a
fresh clone:

```text
$ rm -f memory.db-shm memory.db-wal
$ sqlite3 'file:/tmp/aira-rechunk-measure/memory.db?mode=ro' "SELECT COUNT(*) FROM entries;"
Parse error in 2nd command line argument: unable to open database file (14)
```

I initialized the sidecars with one read-write open (`sqlite3 /tmp/aira-rechunk-measure/memory.db
'PRAGMA user_version;'`). It created `memory.db-shm` (32,768 bytes) and `memory.db-wal` (0
bytes), did not change the main file (SHA-256 re-verified equal to the pristine hash above), and
on this machine (`/usr/bin/sqlite3` 3.54.0, macOS 27.0 build 26A428) the sidecars persisted
afterward. If a later process removes them, re-open the bank read-write once or append
`&immutable=1` to the URI before the read-only checks.

## Decisions and rejected alternatives

- Rejected copying the source's `-shm`/`-wal` onto the clone. Wal-index reuse across database
  files is not a supported operation.
- Rejected switching the clone to `journal_mode=DELETE` just to make read-only opens simple. It
  would change the database's runtime behavior under measurement.
- Rejected `INSERT OR REPLACE` for the two pins even though they were already present. The clone
  is the pinned artifact; an unnecessary write would move its hash.
- Accepted one read-write open to initialize the WAL sidecars, with the main-file hash re-verified
  afterward.

## Access disclosure

Read-only SQLite opens on the source copy bumped its `cb99fe6ecbe0.db-shm` mtime (10:39). The
source `.db` file stayed byte-identical; its SHA-256 was re-computed after all source accesses and
still reads `cb99fe6ecbe0e73b209c83c66827e26e00e0e06719dbab6dab78d44cbc91c790`. No repository
file was touched except this note.

## Raw evidence in the root

`memory.db`, `memory.db-shm`, `memory.db-wal`, `memory.db.sha256`, `source.sha256`,
`clone-stat.txt`, `sha256.log`, `sha256.done`, `run-sha.sh`, `assert-clone.sh`, `nohup.log`,
`coreml-cache` (symlink).

## Acceptance status

| AC | Check | Result |
|----|-------|--------|
| 1 | clone exists; entries == 70763; user_version == 17 | PASS (asserted; self-test red) |
| 2 | coreml-cache symlink; device == coreml; threads == 3 | PASS |
| 3 | chunkBudget count == 0 | PASS |
| 4 | this note records sha/size, provenance, writes, symlink target and caveat | PASS |
