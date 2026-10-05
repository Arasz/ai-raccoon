# Aliased final ingest-scope removal — research (2026-10-05)

## Objective

Fix the 1.57.0 defect where `settings ingest scope remove <alias> <last-path>` reports success but leaves the canonical project's scope active. Write and run a regression test first; only then change production code.

## Constraints

- TDD: the regression must fail against the current implementation for the retained canonical setting, then pass after the fix.
- Limit the production change to canonicalizing project-keyed settings deletion. Preserve existing deletion semantics for unrelated/global keys.
- Use the task worktree and the existing in-process HTTP + SQLite integration fixture; no live bank or default server port.
- Do not change VERSION for this follow-up fix.

## Known unknowns

- Whether resolving the delete key through the existing `Resolve` helper preserves the tested alias-to-`global` safety case. Existing `AliasToGlobal_CannotReadWriteOrDeleteMachineSettings` is the regression gate.
- Whether the focused settings endpoint test is sufficient to cover the CLI's final-path branch. The test will reproduce its actual read/modify/delete sequence using `ServerSettingsStore`; `WatchCommands.ScopeRemoveAsync`'s branch is already covered by separate unit tests.

## Output contract

- One regression test for final-path removal through an alias, observed through the live settings HTTP endpoint and scratch SQLite store.
- The exact test is observed failing before the production edit and passing after it.
- Focused settings endpoint tests pass; report the changed files, commands, and any limitations.

## Stop condition

Stop when the regression fails for the persisted canonical row before the fix, passes after the fix, and the full `SettingsEndpointTests` class passes including alias-to-global deletion and ordinary deletion. No broader settings API redesign is in scope.

## Evidence and sources

- **Measured, previous manual checklist** `docs/work/checklist/2026-10-05-1.57.0-project-id.json`: after `settings ingest scope remove 0199a1b2-0000-7000-8000-000000000004 /tmp`, CLI exit was 0 and printed `removed /tmp …`; `settings ingest scope list` still printed `/tmp`; SQLite still held `ingest.scope.0199a1b2-0000-7000-8000-000000000001 = ["/tmp"]`.
- **Read**, `src/AiRaccoon/Setup/Cli/Commands/WatchCommands.cs`, `ScopeRemoveAsync`: it reads the project key, removes the path, then calls `DeleteSettingAsync(key)` when the updated list is empty.
- **Read**, `src/AiRaccoon/Settings/ServerSettingsStore.cs`, `DeleteSettingAsync`: sends `DELETE /settings?key=<original key>`.
- **Read**, `src/AiRaccoon/Settings/SettingsEndpoint.cs`, `MapSettings`: GET resolves the key and PUT applies the alias map, while DELETE passes the supplied key directly to `store.DeleteSettingAsync`.
- **Read**, `tests/AiRaccoon.Tests/Integration/Setup/SettingsEndpointTests.cs`: existing endpoint coverage includes `Delete_RemovesTheRow`, `Put_UnderAnAlias_WritesAndReadsTheWinnersKey`, `ScopeAddUnderAlias_KeepsWinnersPaths`, and `AliasToGlobal_CannotReadWriteOrDeleteMachineSettings`; no final-scope-delete-through-alias test exists.
- **Verified fix**: `MapDelete` now passes `Resolve(key)` to the migration guard and store deletion. The existing alias-to-global test passed in the 64-test endpoint class, confirming machine-wide `global` remains untouched.
- **TDD evidence**: `ScopeRemoveUnderAlias_RemovesLastWinnerPath` failed before the production edit because the canonical row remained, then passed after the edit. The full `SettingsEndpointTests` class passed 64/64; `git diff --check` passed.
