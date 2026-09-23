# P3 implementation record — F39 no-mint guard

Take-over note: the original P3 lane (d-1561 family) died to an API weekly limit after landing the
guard, the gates and the `TestData` seeding helpers but **before** migrating the fixtures — six
`CliSettingsBackendTests` and four `BackendSessionsTests` fixtures were red, plus the integration
fixtures. This record documents the completed state: the failed lane's work plus the fixture
migration done on take-over, all re-verified.

## What the guard is

- New `src/AiRaccoon/Hosting/Common/BankPresenceGuard.cs`: `EnsureExists(InfrastructureOptions)`
  refuses before any probe or spawn when the resolved root is **not** the default root
  (`DefaultOptions.DataRoot`, compared by expanded path identity — trailing separators and
  `..` detours still exempt) and holds no bank file (`SqliteConnectionFactory.BankPathFor`).
  It only reads: a refusal creates zero files.
- `BankMissingException` message names the resolved path and the remedy
  (`ai-raccoon serve --data-root <root>`).
- Wired at the two client auto-launch composition roots only:
  - `BackendSessions.AcquireBackend` (the proxy's launch path), before the attach/private branch;
  - `CliSettingsBackend.AcquireAsync` (every server-routed settings verb), after the port and
    executable checks so those keep their existing verdicts.
- Exit mapping: `ProxyRunner` and `ConfigCommands` catch `BankMissingException` → `ExitCode.NoBank`
  (22). `serve`, `encryption` and `doctor` never call the guard — they create/open the bank
  themselves, so they stay exempt structurally.
- `ExitCode.NoBank` doc and the how-to exit-code row note the new auto-launch use.

## Gates and their honesty probes

| gate | class | witness |
|---|---|---|
| `EnsureExists_AgainstTheDefaultRoot_NeverThrows_WithoutCheckingTheFilesystem` | `BankPresenceGuardTests` | control: default root exempt |
| `EnsureExists_AgainstADifferentSpellingOfTheDefaultRoot_StillExempts` | `BankPresenceGuardTests` | path identity, not flag-explicitness |
| `EnsureExists_AgainstAnEmptyNonDefaultRoot_ThrowsBankMissing_NamingThePathAndTheRemedy` | `BankPresenceGuardTests` | refusal names path + remedy; root stays empty |
| `EnsureExists_AgainstANonDefaultRootHoldingARealBank_DoesNotThrow` | `BankPresenceGuardTests` | positive control |
| `ProxyAcquire_AgainstAnEmptyExplicitRoot_Exits22_AndCreatesNothing` | `NoMintGuardCompositionTests` | real `ProxyRunner`, real launcher seams, guard never reached the spawn |
| `SettingsVerb_AgainstAnEmptyExplicitRoot_Exits22_AndCreatesNothing` | `NoMintGuardCompositionTests` | real `AppRunner` over the production acquire delegate |
| `NoBank_MapsTo22_InBothCompositionRoots` | `NoMintGuardCompositionTests` | mapping pinned in both composition roots |

Mutation probe (run, then reverted; tree re-verified clean): removing both
`BankPresenceGuard.EnsureExists(config.Options)` calls turns all three composition gates red
(`ProxyAcquire…`, `SettingsVerb…`, `NoBank_MapsTo22_InBothCompositionRoots`), so none of them
passes because the composition merely failed somewhere else.

## Fixture migration (the take-over work)

Every fixture whose subject is a *working* auto-launch must now start from a real bank through the
production factory path; `TestData.SeedBankAsync` / `TestData.CreateTempRootWithBankAsync` are the
one seam (new in `TestData.cs`). Fixtures whose subject is the refusal itself seed nothing.

- `CliSettingsBackendTests` — the six launcher/token tests seed a bank (including the deliberately
  token-less one); `/tmp/unused` roots replaced by seeded temp roots in the three tests that need
  to reach the launcher; port/executable-check tests unchanged (their checks precede the guard).
- `BackendSessionsTests` — same treatment: four fixtures migrated, executable-check tests untouched.
- `CliSettingsSharedBackendTests` — `InitializeAsync` seeds the bank before the three-process
  attach-or-start gate and the disclosure gate run.
- `CliSettingsTokenExposureTests` — the squatter-acceptance fixture seeds a bank so the acquire it
  documents still runs. **Its deletion per D8 is deliberately left to P4**, where the proof-gated
  acquire reverses the behaviour it documents.
- `QuietLoggingTests` — both proxy relay fixtures seed banks; without it the loud control failed
  and the quiet gate passed for the wrong reason (refusal instead of relay).
- `BackendSessionsTokenExposureTests` (squatter gate), `ProxyPrivateBackendLifetimeTests`
  (private-backend lifetime gate), `ProxyLaunchE2ETests`, `ProxySpawnedBackendE2ETests`,
  `ProxyWireE2ETests`, `ProxyTokenRefusedE2ETests` — seeded so the Nightly E2E lane keeps its
  subject. The identity-key half of those fixtures (A11) still lands with P4.

## Fixture seeding deliberately left to P4 (A11/D8)

- `CliSettingsTokenExposureTests` deletion (its premise is the behaviour ADR-0106 reverses).
- `ProxyWireE2ETests` / `ProxyTokenRefusedE2ETests` remote-attach expectations: these run a **bare**
  proxy and expect it to dial the fixture's in-process/gated backend. Under ADR-0105's private-spawn
  default (commit `d09d4ead`, #643) that expectation has been false since F70 — they are
  Nightly-only, so no gate saw it. They are *not* P3 regressions; P4's attach-or-start revert is
  what makes them true again. Verified pre-existing by source history, not fixed here.

## Lane results after the take-over

- `dotnet build`: 0 warnings / 0 errors.
- `Speed=Fast&Performance!=Benchmark`: 3930 total, 3929 passed, 1 skipped (env-gated fixture) — 0 failed.
- `Category=bdd`: 184 total, 179 passed, 5 skipped — 0 failed.
- `Speed=Slow&Performance!=Benchmark`: 1116 total, 1112 passed, 4 skipped — 0 failed
  (re-run after the last fixture edits).
- Targeted Nightly E2E re-runs: `ProxyLaunchE2ETests`, `ProxySpawnedBackendE2ETests` green;
  the two F70-preexisting classes recorded above stay red for P4.
- `CliCommandsDoNotOpenTheBankTests`, `serve`/`encryption` creation controls: green inside the
  fast lane.

No new EventIds (the guard refuses by exception; 690-691 stay reserved for P4 logging).
VERSION untouched.