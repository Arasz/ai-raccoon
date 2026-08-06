# Research: sqlite3_rsync (sqlite.org/rsync.html) as AiRaccoon's DB sync mechanism

**Date:** 2026-08-06 **Question:** Should AiRaccoon adopt the SQLite rsync technique — the `sqlite3_rsync` utility documented at https://www.sqlite.org/rsync.html — for syncing its memory bank?

## Findings

### F1 — sqlite3_rsync syncs plain, unencrypted, live databases correctly [MEASURED]

The tool does what the page claims for ordinary databases: it copies a consistent snapshot of the origin onto the replica while both are open. In a local-to-local test, a WAL-mode database that had grown by two rows after the replica was
copied synced cleanly; the replica read back all three rows.

**Evidence:** `sqlite3_rsync` 3.53.4 from `sqlite-tools-osx-x64-3530400.zip` (sqlite.org, 2026/ download dir), run on this Mac (macOS 26.5.2, arm64; x64 binary under Rosetta). In `/tmp/sync-test`:
`sqlite3 origin-plain.db "PRAGMA journal_mode=WAL; CREATE TABLE t(x); INSERT INTO t VALUES (1);"`, copy to replica, `sqlite3 origin-plain.db "INSERT INTO t VALUES (2),(3);"`, then `sqlite3_rsync -v origin-plain.db replica-plain.db` → exit
0, "sent 4,114 bytes, received 43 bytes… speedup is 1.97"; `SELECT count(*) FROM t` on replica → 3.

### F2 — The tool's option surface has no key or passphrase flag [MEASURED]

There is no way to hand the tool a SQLCipher key. Its complete option list is `--exe`, `--help`, `--port`, `--protocol`, `--ssh`, `-v`, `--version`, `--wal-only`.

**Evidence:** `./sqlite-tools/sqlite3_rsync --help` (3.53.4, conditions as F1) — full output captured; no `--key`/`--password`/encryption-related option exists.

### F3 — sqlite3_rsync hard-fails on a SQLCipher-encrypted database [MEASURED]

Given an encrypted origin and an encrypted replica, the tool cannot even open them: it fails with "file is not a database" on its very first query, transfers zero bytes, and leaves the replica untouched. It then refuses to sync ("Databases
were not synced due to errors"). SQLCipher encrypts the entire first page — the file's magic string and header are ciphertext — so the tool's header parsing (`PRAGMA page_count`, page-size and WAL detection) has nothing readable to work on.

**Evidence:** `sqlcipher` CLI 4.17.0 (Homebrew; SQLCipher 4, AES-256-CBC) created `/tmp/sync-test/enc-origin.db` in WAL mode with key `test-key-123` (`PRAGMA key`, `PRAGMA journal_mode=WAL`, table t, one row). `xxd -l 64` shows all 64
header bytes ciphertext. After `INSERT` of two more rows on the origin: `sqlite3_rsync -v enc-origin.db enc-replica.db` → "unable to prepare SQL [PRAGMA page_count]: file is not a database", exit 1, "sent 0 bytes, received 0 bytes". Replica
still opens with the key and returns its original 1 row. Run 2026-08-06, same machine and binary as F1.

### F4 — The chacha20 cipher used by AiRaccoon's SQLite3MC bundle is blocked by the same mechanism [INFERRED]

The measured failure is at the container level — the tool cannot read a SQLCipher-format file header regardless of cipher — and SQLite3MC is a SQLCipher-format fork; its chacha20 mode still encrypts page 1 wholesale, so the identical
failure is expected. It was not directly tested because no chacha20-capable CLI ships with SQLite3MC.PCLRaw.bundle (a native library, not a CLI tool).

**Reasoning from:** F2/F3 (measured header-level failure), the SQLCipher page-1-encryption format (read in F3's test output), and the project fact that the app uses the SQLite3MC.PCLRaw.bundle 2.4.0 native library with cipher chacha20
(docs/work/2026-08-06-sqlite3mc-2.4.0-upgrade.md).

### F5 — The protocol is one-directional snapshot copy with no merge logic [READ]

`sqlite3_rsync` makes REPLICA a copy of ORIGIN as it existed when the command started; REPLICA is read-only while it runs. There is no concept of merging two sides that both wrote since the last sync, no tombstones, no conflict policy.
Alternating the direction between two machines yields whole-file last-writer-wins.

**Evidence:** https://www.sqlite.org/rsync.html §2 ("REPLICA becomes a copy of a snapshot of ORIGIN as it existed when the sqlite3_rsync command started"), §3 ("While sqlite3_rsync is running, REPLICA is read-only"). Fetched 2026-08-06;
page updated 2025-11-13.

### F6 — AiRaccoon's implemented sync is bidirectional row-merge over CAS object storage, built to avoid exactly the loss F5 would reintroduce [READ]

The shipped sync (`memory_sync`) is "row-merge sync over object storage: VACUUM INTO snapshot → pull → ATTACH+merge → push If-Match" with per-row LWW, tombstones for delete propagation, and ETag compare-and-swap against the cloud copy. That
design exists because whole-file last-writer-wins silently drops one side's writes — the one failure a memory bank cannot tolerate. `sqlite3_rsync` has none of that machinery; adopting it would mean replacing the implemented, tested sync
with a strictly weaker one.

**Evidence:** `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:6` (class summary), `:49-56` (VACUUM INTO snapshot), `:80,211` (PRAGMA quick_check), `:110,164` (merge snapshot); `AzureBlobCloudStore.cs` and `S3CloudStore.cs` implement the
CAS store behind `ICloudStore`. Design rationale: `.ai-badger/skills` → software-development/sqlite-sync-design (row-merge over whole-file transport; "remote wins" silently drops the losing side's writes).

### F7 — The transport is SSH peer-to-peer, not object storage; deployment and platform constraints apply [READ]

The tool syncs between two machines over SSH, requires the binary installed on both ends (or `--exe`), and the author states he has never gotten SSHD to work on Windows — remote Windows is unsupported. For AiRaccoon's model (install ↔
always-available cloud bucket), adopting it means standing up and operating a reachable SSH host — the zero-infra property of the bucket design is lost.

**Evidence:** https://www.sqlite.org/rsync.html §3 ("On the remote system, this utility must be installed in one of the directories in the default $PATH for SSH"; "The writer of this document has never had any success in getting SSHD to run
on Windows"), §4 (macOS PATH augmentation workaround).

### F8 — The live bank is 29.5 MB and currently unencrypted on this machine [MEASURED]

The bank at `~/.ai-raccoon/memory.db` is 29,491,200 bytes (2026-08-06), its header is plaintext SQLite, and `sqlite3` opens it and reads `sqlite_master` with no key — so `sqlite3_rsync` would technically work against *this* bank today. It
does not matter: the product's encryption feature means any sync design must handle encrypted banks, which F3 rules out.

**Evidence:** `ls -la ~/.ai-raccoon/memory.db` → 29,491,200 bytes; `xxd -l 64` of the file shows a normal SQLite header (page size 4096 at offset 16-17, write-version 2);
`sqlite3 ~/.ai-raccoon/memory.db "SELECT count(*) FROM sqlite_master;"` → 47, no key. 2026-08-06.

### F9 — The bandwidth win is moot at bank scale [INFERRED]

The tool's headline efficiency — a 500 MB database syncing with ~20 KB of traffic — only helps a delta-capable transport over a large file. The object-storage path uploads a whole VACUUM INTO snapshot on every push regardless of how few
pages changed, and the measured 29.5 MB bank is seconds to upload on broadband; nothing at this scale is saved by page-level deltas.

**Reasoning from:** the measured bank size (F8), the READ §5 claim of sqlite.org/rsync.html (20 KB per 500 MB), and SyncService.cs:56's whole-snapshot push (READ).

## Still open

- The chacha20 cipher was not tested directly (only SQLCipher AES via the brew CLI, which lacks chacha20). The failure mechanism is container-level so the transfer is near-certain; a definitive run needs a SQLite3MC build exposing a CLI,
  which the NuGet bundle does not ship.
- The §5 bandwidth figure (20 KB per 500 MB) was not reproduced at scale — my measured case was an 8 KB database. It is moot given F3, but it is the claim most likely to be quoted from this page.
- "Remote Windows unsupported" is the author's statement; not re-verified independently.
- The live bank currently shows WAL sidecars (`memory.db-wal`, `-shm` present) while the app's documented default is DELETE mode; irrelevant to the verdict (encryption fails first), but the observed mode mix is unaccounted for.

## Verdict

Do not adopt. The blocker is decisive and measured: sqlite3_rsync cannot open a SQLCipher-format database (F2+F3), and the product's encrypted-at-rest bank is exactly that format. Independently, the tool's one-directional, merge-free
snapshot semantics (F5) contradict the shipped row-merge + tombstone sync (F6) and would reintroduce silent memory loss on concurrent installs; its SSH peer-to-peer transport (F7) replaces zero-ops object storage with a host to run; and its
bandwidth advantage does not apply at this scale (F8). The tool is well-suited to its actual niche — keeping a read replica of an unencrypted, single-writer database — which is not AiRaccoon's profile.

Grade mix: 4 measured, 3 read, 2 inferred, 0 unverified.
