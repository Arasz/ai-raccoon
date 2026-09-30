# Copy tooling: target WAL checkpoint + sha256 sidecar

Date: 2026-09-30. Task: `air-254-vs-1022-retrieval-measurement-809` (issue #809), step `copy-tool-pin`.

Data root: `/tmp/continue-testing-algorithm/datasets` (the tool's default copy target; tests write under pytest `tmp_path`). The live bank stays at `~/.ai-raccoon/memory.db`, is opened only through a `mode=ro` URI, and is never written.

## What already existed, and the delta

Most of the read-only discipline was already in `scripts/retrieval_tuning/make_memory_copy.py`: the live source opens only via `file:...?mode=ro` (`open_readonly(..., strict=True)`), and the tool already computed counts, integrity, spot-check hashes and the retrieval/fusion printout. The missing pieces were the two this step names.

1. **Target WAL checkpoint.** A probe on a synthetic WAL bank confirmed that `Connection.backup()` copies the source's WAL flag in page 1, so the target ends up persistently in WAL mode, and the last-close checkpoint does not clear that flag. `run_copy_and_verify()` now calls `checkpoint_target()` on the temp target before the atomic rename: `PRAGMA wal_checkpoint(TRUNCATE)` followed by `PRAGMA journal_mode=DELETE`. The mode switch persists in the database header, so no later reader can find pending WAL frames and a sha256 of the main file describes the whole copy. That durability matters for the downstream `copy-1022` step, which takes its pin after convergence writes.
2. **Sidecar pin.** `run_copy_and_verify()` writes `<target>.pin.json` (override with `--pin`) recording sha256, bytes, counts, `user_version`, journal mode, checkpoint frame count and a settings snapshot. The write is atomic (temp file plus `os.replace`), matching the copy itself. `main()` prints a `pin:` line.
3. **Settings snapshot with a secret guard.** The downstream `copy-1022` AC1 asks for the `embedding.*` settings snapshot. A blanket `embedding.%` dump would copy `embedding.apiKey`, a persisted secret (see `EmbeddingSettingsKeys.ApiKey`; `DoctorCommands` treats it as secret). `read_settings_snapshot()` queries `retrieval.%` / `fusion.%` / `embedding.%` and drops any key whose name marks a credential (`apikey`, `secret`, `token`, `password`, `passwd`, `credential`).
4. `verify_copy()`'s report gained `settings_snapshot` and `user_version`. Every change is additive: existing report keys, CLI flags and call signatures keep working.

### Rejected

- **Asserting only "no `-wal` file exists".** Old code passes that by accident: the last connection close checkpoints and deletes the WAL. The new test pins the durable property instead (`journal_mode == "delete"`, `wal_frames == 0`, then a `mode=ro` read of the main file that returns every row).
- **Leaving the target in WAL mode with a truncated WAL.** `wal_checkpoint(TRUNCATE)` alone satisfies "no pending frames" at that instant, but any convergence write puts frames back and the main-file sha goes stale. `journal_mode=DELETE` makes it durable.
- **Dumping the whole `settings` table.** It contains `embedding.apiKey`; a full dump leaks a credential into a plain JSON file.
- **Pinning before verification.** Verify only reads, so the pin is taken after it and records `verified`; a pin is still written on verification failure so a bad artifact stays traceable.

Verified with a probe (scratch `/tmp/walprobe`, source WAL with frames held open, `Connection.backup` into a fresh target):

```text
src journal_mode: ('wal',)
tgt journal_mode before backup: ('delete',)
tgt journal_mode after backup (dst open): ('wal',)      <- backup copies the WAL flag
tgt wal_checkpoint while open: (0, 0, 0)
files after all close: ['src.db', 'tgt.db']             <- auto-checkpoint, flag still wal
tgt journal_mode reopen: ('wal',)
after journal_mode=DELETE: ('delete',)
tgt wal_checkpoint after delete-mode: (0, -1, -1)       <- no WAL at all
files after delete-mode close: ['src.db', 'tgt.db']
```

Live bank, read-only probe (`mode=ro`, no write): `PRAGMA journal_mode` → `wal`. So the real 848 MB copy takes the checkpoint path.

## Sidecar schema

CLI demo output for a scratch fixture (`/tmp/pin-demo`, 9 entries, 7 embedded, user_version 17, WAL source):

```json
{
  "bytes": 16384,
  "copiedAt": "2026-09-30T08:48:02+00:00",
  "embedded": 7,
  "entries": 9,
  "journalMode": "delete",
  "path": "/private/tmp/pin-demo/out/memory-copy.db",
  "settings": {
    "embedding.chunkBudget": "1022",
    "embedding.device": "coreml",
    "retrieval.structureAlpha": "0.5"
  },
  "sha256": "61fca233f64d83e3147d577568205c97088de654ab35262e9f9542b238a6c3ad",
  "sourcePath": "/private/tmp/pin-demo/live.db",
  "userVersion": 17,
  "vecEntries": null,
  "verified": true,
  "walFrames": 0
}
```

| Field | Meaning |
|---|---|
| `path`, `sourcePath` | Resolved target and live-source paths. |
| `sha256`, `bytes` | Hash and size of the final target file, taken after the checkpoint. |
| `entries`, `embedded`, `vecEntries` | Copy counts (same shape as the verification report). |
| `userVersion` | `PRAGMA user_version` of the copy. |
| `journalMode`, `walFrames` | Checkpoint result: `delete` and `0` on a clean copy. |
| `settings` | Non-secret `retrieval.%` / `fusion.%` / `embedding.%` rows, keyed by name. |
| `verified` | Whether the copy passed the verification pass. |
| `copiedAt` | ISO-8601 UTC timestamp of the pin write. |

For the `copy-1022` consumer, the load-bearing fields are `.sha256`, `.entries`, `.userVersion` and `.settings["embedding.chunkBudget"]` / `.settings["embedding.device"]`. The fixture also carried `embedding.apiKey='sk-must-not-leak'`; it is absent from the sidecar above.

## Tests

New tests in `scripts/tests/test_retrieval_tuning_copy.py`:

| Test | Status |
|---|---|
| `test_sidecar_records_sha_counts_settings` | RED-first |
| `test_target_has_no_pending_wal_frames` | RED-first |
| `test_strict_read_only_live_source` | Green pin |

One correction to the step text: `test_strict_read_only_live_source` did **not** exist at the branch base, despite the step saying it "stays green" (it appears nowhere in the file or `git log`). I added it and it passed against the pre-change code, so it is a pin of existing behavior and cannot be RED. It spies on `sqlite3.connect` and requires every connection touching the live path to use the exact `file:<resolved>?mode=ro` URI with `uri=True`. AC1 itself names only the two `open_readonly` tests, both untouched and green.

### RED (implementation not yet written)

Command: `python3 -m pytest scripts/tests/test_retrieval_tuning_copy.py -q`

```text
        report = mc.run_copy_and_verify(str(live), str(target), sample_size=2, rng=random.Random(7))
    finally:
        writer.close()

    assert report["ok"] is True
>   assert report["journal_mode"] == "delete"
           ^^^^^^^^^^^^^^^^^^^^^^
E   KeyError: 'journal_mode'

scripts/tests/test_retrieval_tuning_copy.py:349: KeyError
______ test_sidecar_records_sha_counts_settings ______

    pin_path = Path(str(target) + ".pin.json")
>   assert pin_path.exists()
E   AssertionError: assert False
E    +  where False = exists()
E    +    where exists = PosixPath('.../out/memory-copy.db.pin.json').exists()

scripts/tests/test_retrieval_tuning_copy.py:376: AssertionError
=========================== short test summary info ===========================
FAILED scripts/tests/test_retrieval_tuning_copy.py::test_target_has_no_pending_wal_frames
FAILED scripts/tests/test_retrieval_tuning_copy.py::test_sidecar_records_sha_counts_settings
2 failed, 17 passed in 0.09s
```

### GREEN (implementation written)

```text
$ python3 -m pytest scripts/tests/test_retrieval_tuning_copy.py -q
...................                                                      [100%]
19 passed in 0.06s
```

### Every new gate was broken on purpose

Each mutation was applied to `make_memory_copy.py`, the narrowest test run, then the file restored. All three backups and the final file hash to `99e189881bd14ce00166deb940b7b495f85cf0e92a2eb4a5b0e751aeb09145f4`.

1. Checkpoint call replaced with a fabricated `{"journal_mode": "wal", "wal_frames": -1}`:

```text
E         - delete
E         + wal

scripts/tests/test_retrieval_tuning_copy.py:349: AssertionError
FAILED scripts/tests/test_retrieval_tuning_copy.py::test_target_has_no_pending_wal_frames
1 failed in 0.03s
--- restored, re-run:
1 passed in 0.03s
```

2. `write_sidecar(pin_path, pin)` call removed:

```text
E       AssertionError: assert False
E        +  where False = exists()
E        +    where exists = PosixPath('.../out/memory-copy.db.pin.json').exists()
FAILED scripts/tests/test_retrieval_tuning_copy.py::test_sidecar_records_sha_counts_settings
1 failed in 0.07s
--- restored, re-run:
1 passed in 0.02s
```

3. `_is_secret_key()` forced to `False`:

```text
>       assert "embedding.apiKey" not in pin["settings"]
E       AssertionError: assert 'embedding.apiKey' not in {'embedding.apiKey': 'sk-secret-must-not-leak', 'embedding.chunkBudget': '1022', 'embedding.device': 'coreml', ...}
FAILED scripts/tests/test_retrieval_tuning_copy.py::test_sidecar_records_sha_counts_settings
1 failed in 0.03s
--- restored, re-run:
1 passed in 0.02s
```

## Evidence per acceptance criterion

**AC1** (read-only behavior stays pinned), command plus output:

```text
$ python3 -m pytest scripts/tests/test_retrieval_tuning_copy.py::test_open_readonly_rejects_writes \
    scripts/tests/test_retrieval_tuning_copy.py::test_open_readonly_uri_uses_mode_ro \
    scripts/tests/test_retrieval_tuning_copy.py::test_strict_read_only_live_source -q
...                                                                      [100%]
3 passed in 0.02s
```

**AC2** (sidecar + no pending WAL frames), RED above; green subset:

```text
$ python3 -m pytest scripts/tests/test_retrieval_tuning_copy.py::test_sidecar_records_sha_counts_settings \
    scripts/tests/test_retrieval_tuning_copy.py::test_target_has_no_pending_wal_frames -q
..                                                                       [100%]
2 passed in 0.03s
```

Full AC check, `python3 -m pytest scripts/tests/test_retrieval_tuning_copy.py -q`: `19 passed in 0.06s`.

**AC3** (live bank only ever `mode=ro`):

```text
$ grep -q 'mode=ro' scripts/retrieval_tuning/make_memory_copy.py && echo exit 0
exit 0
$ grep -n 'mode=ro' scripts/retrieval_tuning/make_memory_copy.py
5:source is opened ONLY through a read-only URI connection (``file:...?mode=ro``),
62:    database with no -shm file cannot open via ``mode=ro`` (SQLite must create
66:    uri = f"file:{Path(path).resolve()}?mode=ro"
```

End-to-end CLI demo on the scratch fixture (`make_memory_copy.py --live /tmp/pin-demo/live.db --target /tmp/pin-demo/out/memory-copy.db --sample-size 2`) printed `pin: /tmp/pin-demo/out/memory-copy.db.pin.json`, `VERIFIED: copy is healthy`, and a read-only probe of the target returned `delete`, `0|-1|-1`, `9` for `journal_mode`, `wal_checkpoint` and the entry count. The target directory held only `memory-copy.db` and `memory-copy.db.pin.json`, with no `-wal` or `-shm`.

## Review

Review-tests finding file, written by a separate QA pass and not by this step: `docs/work/2026-09-30-review-tests-copy-tool-pin.md`.

## Open items and labels

- MEASURED: the live bank is WAL (`PRAGMA journal_mode` over `mode=ro` returns `wal`), so the real copy takes the checkpoint path.
- HYPOTHESIS: hashing the ~848 MB copy adds a few seconds to the run; the suite only exercised fixture-sized files, so the 848 MB timing was not measured here.
- The `--pin` override is exercised only by reading (the tests use the default path). If a downstream step needs a custom sidecar location, that invocation will be its first execution of the override.