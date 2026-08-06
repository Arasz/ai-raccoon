# Test-harness QA: playbook and a worked case study

Companion to SKILL.md step 6. Captures the full-suite QA pass over the
agent-memory feature harness (`AiRaccon/.ai-badger/worktrees/agent-memory-server`,
154 tests, xunit.v3 + Shouldly) and the reusable playbook that emerged.
Read-only review; findings were severity-ranked with file:line and ended with
the exact list of new tests the fix batch must add.

## The playbook

1. **Full pairing read.** Read EVERY test file AND every production file the
   tests exercise (snapshot `wc -l` per file first, then the whole tree —
   tests and `src/` in equal measure). Skip nothing: the gap is usually in the
   file you'd skip. ~2,100 lines of tests + ~2,000 lines of src is a normal
   budget for this.
2. **Assertion taxonomy** for each integration test (the per-test table is the
   core artifact):
   - **TAUTOLOGY** — asserts a field of the record the method constructed
     itself (e.g. `shared.Context.ShouldBe("shared")` where `ShareAsync`
     returns `new MemoryEntry(..., ContextNaming.SharedContext, ...)`). Green
     while the feature does nothing. Pure tautologies and missing tests are the
     same failure.
   - **WEAKLY-OBSERVABLE** — real DB read-back, but the assertion can't see the
     regression (two writes asserting equal content-derived hashes passes even
     when dedup inserts duplicate rows — the read-back is `ORDER BY rowid DESC
     LIMIT 1`, so a duplicate row never surfaces; nothing asserts
     `EntryCount == 1`).
   - **OBSERVABLE** — fresh re-query through a different SQL path or a
     consuming operation (delete test re-reads stats count; list test re-reads
     via a different statement than the write path used).
3. **Fake fidelity audit.** For every fake, ask: what would the REAL backend do
   for this input?
   - A `FakeMetaStore` returning rating 0.1 for ANY hash (even non-existent
     ones) let a wrong "missing meta row ⇒ degradable" `SweepService` pass —
     the real store returns `null` and the service falls back to
     `DefaultBaseScore` (0.5). Fakes must return null for unknown data, and a
     test must pin the real fallback.
   - A fake that records "which contexts were queried" can only prove the loop
     never calls X. It cannot model backend semantics like `memory_delete(hash)`
     being GLOBAL across contexts — so the "shared is sweep-exempt" guarantee
     was reduced to a loop-shape check while a real sweep still deletes the
     shared copy of identical content.
4. **Skip honesty.** `return;` on unavailable native extensions reports PASSED
   with zero work. A broad `catch (SqliteException)` around the probe also
   swallows broken-but-loadable extensions that fail at the probe's own
   `memory_*` call — CI with a broken extension is green. Gate the skip on the
   missing precondition (extension files absent), let other exceptions fail,
   use `Assert.Skip`, and count executed tests so "0 ran" is visible.
5. **Snapshot tests.** `MemorySql.InsertText.ShouldBe("SELECT ...")` fails only
   on a literal text edit — it never validates executability. Cross-check which
   constants have NO snapshot and NO behavior test: the broken statements
   (`memory_embed_pending`, `defer_embeddings`, `set_model`, `set_apikey`,
   ingest, `list_files`) had none.
6. **AC trace table.** Per AC: scenario → implementation path → test that fails
   if reverted. When none exists, write the exact new test: `name — asserts —
   why it catches the bug`. Deliver as a numbered list the fix batch can copy,
   and end with an explicit verdict per claimed capability ("would the harness
   catch a regression in X?").

## Bug patterns found (worked case study)

1. **Share/promote no-op + tautology.** Upstream `memory_add_text` dedups
   GLOBAL by content hash (`dbmem_database_check_if_stored`, no context
   filter) — the "promote to shared" insert silently creates no row. The only
   share test asserted the code-constructed return (`shared.Context`).
   Fix-test: re-query `ListContextAsync(shared)` and
   `GetStatsAsync().Contexts` after sharing.
2. **`embed_pending(NULL)` hits a strict-argument error.** `memory_embed_pending(@limit)`
   with `DBNull` errors upstream ("expects a positive INTEGER"). No test called
   `EmbedPendingAsync` at all. Fix-test: call with null limit, assert
   `Processed >= 1 && Pending == 0`.
3. **Per-connection option clobber.** The connection factory ran
   `memory_set_option('defer_embeddings', 1)` on EVERY open; options PERSIST
   (`dbmem_settings`), so the configure step's `defer=0` is clobbered on the
   next connection. No test opened a second connection after configure (factory
   tests even used a no-op extension loader, so the defer default itself was
   untested). Fix-test: configure → NEW factory/store → write → pending stays 0.
4. **Global delete vs sweep exemption.** `memory_delete(hash)` is global; sweep
   exempts `shared` only by never listing it, so a project sweep deletes the
   shared copy of identical content. Fake-side "shared never queried" can't see
   it. Fix-test: real sweep with shared+project copies of one hash, assert the
   shared row survives.
5. **Dead provisioning masked by test setup.** Integration tests copied
   pre-provisioned extension files from `~/.ai-raccon/extensions/<rid>` into
   the temp data root; the production `Program.cs` never calls
   `ExtensionProvisioner` at all. First-run open throws. Grep for the
   production caller of anything the tests set up manually.
6. **Skip-the-broken-SQL pattern.** Snapshot tests covered write/delete/search-
   contains but omitted exactly the statements that were wrong (embed_pending,
   defer_embeddings, set_model, set_apikey).

## Tooling notes

- `read_file` and terminal `grep`/`cat -v` mask secret-looking substrings as
  `***`; only `sed -n 'N,Mp' file | od -c` shows raw bytes. `"apikey:***` in
  test source was display masking, not corruption (`$"apikey:{apiKey}"` on
  disk).
- The `code-review-evidence` skill's `references/sqlite-memory-semantics.md`
  (verified upstream facts for sqlite-memory/vector/sync) was the check engine
  for this review — confirm dedup scope, option persistence, strict-argument
  requirements, and delete granularity from upstream source, not the wrapper's
  comments.
