# Completing an incomplete refactor (worked recipe, 2026-08-06)

Session-tested on ai-raccoon `task/encryption-commands-rework`: the user marked the
encryption refactor "finished" (@git:1) but the commit was build-red, the config-verb
dispatch was deleted, and ~25 test files were broken. This file carries the lessons that
made the completion run safe. Also covers a delegate_task briefing failure.

## "Part is finished" = "part is drafted"

Verify before planning, no matter how confident the claim:

- `dotnet build` stops at the FIRST failing project. A CS9113 in `Infrastructure` masked
  broken `src/AiRaccoon` code, a deleted `ConfigVerbRunner`, and the whole test project.
  The visible error list is NOT the inventory.
- Roslyn suppresses method-body binding errors while declaration-phase errors exist. The
  true 393-error inventory appeared only after the old-shape stub types were fixed.
- Enumerate the OLD API surface with repo-wide greps (ctor arities, interface method
  shapes, deleted constants) — including files the author never touched. E.g.
  `grep -rn 'new EncryptionKeyResolver\|GetPassphrase()' --include='*.cs' src tests`.
- READ the entry-point file (Program.cs) for deleted dispatch branches. A test referencing
  a class that no longer exists (`RunWithBitwardenSecretsManagerEncryptionKey.RunAsync`)
  is compile-proof of incomplete work.
- Check the diff's deleted files list: `git show --stat <commit> | grep deleted`.

## delegate_task `tasks[]` batch mode drops the top-level brief

The child sees ONLY its own task entry's `goal`/`context`. Dispatching
`delegate_task(tasks=[{goal, context}])` with the plan in the TOP-LEVEL context leaves the
child searching the repo for "WP1|WP8" with nothing to find.

Recovery that worked (do NOT kill a floundering child before checking its transcript):
1. The child itself read the parent session's DB —
   `sqlite3 ~/.hermes/state.db "SELECT content FROM messages WHERE session_id='<parent-session-id>'"` —
   and the delegation-cache summary at `~/.hermes/cache/delegation/subagent-summary-*.txt`.
   It reconstructed the full architect plan from both.
2. I dropped the full plan into the worktree's gitignored `.ai-badger/task-tracking/` dir —
   a location the child had already searched — and it picked it up on the next search.

Prevention: with the `tasks` array, the complete brief goes INSIDE each task's own
goal/context fields. Never rely on the top-level fields in batch mode.

## "Test project compiles / targeted tests pass" is NOT the gate

The implementer reported WP1-7 done with the test project building clean and 34 targeted
tests green. The orchestrator's full-suite run still exposed three production bugs the
targeted runs never touched:

1. `AddSingleton<IBundledModel, BundledModel>()` where BundledModel takes
   `IHttpClientFactory` — nothing registered it (package reference ≠ registration), so
   EVERY DI-hosted server boot failed with "Unable to resolve service for
   'System.Net.Http.IHttpClientFactory'". Fix: `services.AddHttpClient()`.
2. `app.Services.GetRequiredService<ILogger>()` (non-generic) — default hosts do NOT
   register the non-generic `ILogger`; the server crashed before startup. Fix:
   `ILoggerFactory.CreateLogger("Program")`. Only E2E catches Program-top-level DI bugs.
3. `CliArgs.ReadOptions` called `OptionResult.GetValueOrDefault<T>()` even when the parse
   had errors — System.CommandLine THROWS `InvalidOperationException` for invalid option
   values (`--transport ftp`), so TryParse crashed instead of returning errors. Fix:
   try/catch → defaults; errors live in the Errors list, never thrown.

Also expect MECHANICAL TEST MIGRATIONS to silently change semantics: swapping
`new StubEnvProvider(DerivedRawKey)` for the shared `Resolver()` re-keyed the bank
(env-passphrase instead of the derived key) — the tests still passed for the wrong reason
in one case (both keys produce SqliteErrorCode 26). A stub pinned to a specific key is NOT
the shared resolver; once the resolver consults a sidecar, the double needs a
sidecar-independent state (stub `IEncryptionState` returning `EncryptionData.None`).

Triage pattern: group full-suite failures by exception signature before fixing — 253
failures sharing one `ArgumentNullException` at the same frame = ONE fix (a resolver
double that no longer matched the provider-selection semantics after the "absent sidecar →
env" mapping).

## Process-level tests: hermetic child environment (2026-08-06)

To pin Program.cs error mapping / exit codes with a real process launch, the child must
NOT see the dev machine's real tooling:

- **Prepending an empty dir to the child's PATH does NOT simulate "command missing".**
  PATH lookup falls through to the real command later in the chain — the test found the
  real `bws` on the dev machine and passed solo for the wrong reason (its failure happened
  to produce the same exit code), then behaved nondeterministically under load (empty
  stderr). 
- Correct recipe: resolve the launcher by absolute path (scan the parent PATH for the
  executable: `PATH.Split(Path.PathSeparator).Select(d => Path.Combine(d, "dotnet"))
  .FirstOrDefault(File.Exists)`), then set the child's `PATH` = the controlled dir(s) +
  the system dirs shell scripts need (`/usr/bin:/bin` on unix; `Path.PathSeparator` join).
  Explicitly blank ambient secrets in the child (`BWS_ACCESS_TOKEN`, passphrase env vars).
- Capture BOTH stdout and stderr and include both in assertion messages; `dotnet exec`
  banners ("Using launch settings from …") pollute stdout, so assert with contains, never
  exact equality.
- Skip (early-return) on Windows if the fake is a shell script.

## Full-suite flake triage

A watch integration test failed 2 of 3 full runs (15 s StepUntilAsync timeout) but passed
3/3 solo in ~300 ms. Verdict: pre-existing load-flake, not a regression:

- Pass the test solo 3× (deterministic pass = timing, not logic).
- Diff its timing structure against the pre-refactor baseline:
  `git show <old-commit>:<file> | grep -n 'StepUntilAsync\|deadline\|TimeSpan'` —
  byte-identical structure = the PR didn't change it.
- Confirm the code under test is untouched by the diff (`git diff <base>..HEAD -- <dir>`).
- Report honestly ("1 of N full runs failed on a known load-flake, passes solo 3/3")
  instead of expanding scope to "fix" it or silently re-running until green.

## Review gate: error-path mirrors must discriminate causes (S1)

A BDD/test-side mirror of the production error mapping that catches `Exception` broadly
makes every failure scenario pass on ANY exception (wrong key, corrupt bank, unrelated IO
— all green). Re-add typed filters so a scenario can only pass for its stated cause:

```csharp
catch (SqliteException ex) when (ex.SqliteErrorCode == 26)
{
    return $"… mismatch message …";
}
catch (Exception ex)
{
    return ex.Message; // anything else surfaces verbatim — the mirror never masks it
}
```

Same idea for the production side: put the cause in the user-visible line
(`"… source key: {Error}"` with `ex.Message`), and keep LoggerMessage EventIds as literals,
not aliases of exit-code constants (changing one silently changes the other).

## Other session notes

- FakeLogger (`Microsoft.Extensions.Logging.Testing`): `Collector.LatestRecord` THROWS
  `InvalidOperationException("No records logged")` on an empty collector — assert
  `Collector.Count.ShouldBe(0)` for "nothing logged".
- `CreateLogger<T>()` is illegal for a static class — use `CreateLogger("CategoryName")`.
- BDD feature-context ctor ordering: derive the sidecar/bank path from the OPTIONS
  (`SqliteConnectionFactory.BankPathFor(options)`), never from a property that reads a
  field assigned later in the ctor (`BankPath => Bank.BankPath` NREs every scenario via
  Hooks).
- `dotnet run` prints "Using launch settings from …" to stdout — exact-match assertions
  on captured CLI output must grep for the expected line instead.
- **Post-`finish` re-verification:** `tracker finish` deletes the worktree; the final
  state lives only on the pushed branch. Re-create it for fresh evidence with
  `git worktree add .ai-badger/worktrees/<id> origin/task/<branch>` — and copy the
  gitignored assets again (e.g. the ONNX `Models/` dir): a fresh checkout fails
  model-dependent tests (BundledModelLoggingTests' all-present case) until the copy lands.
