# TDD cluster discipline in delegated plan execution (worked example)

Session: P2 (factory routing) + P3 (CLI) of a reviewed plan in a .NET 10 ai-badger
worktree — six fact clusters (a–f), strict RED→GREEN→commit per cluster, targeted
`dotnet test --filter` gate per cluster and a clean `dotnet build` (0 warnings,
warnings-as-errors) at the end.

## The ordering trap (parse-surface clusters must run first)

- Cluster b (ConfigCommands `sync add azure` behavior) ran through a `Run` helper that
  asserts `CliArgs.Parse(args).Errors.ShouldBeEmpty()` — so the `sync add azure`
  subcommand had to exist in CliArgs BEFORE any behavior test could fail.
- Cluster f (CliArgs parse facts: `Parse_SyncAddAzure_ParsesContainerAndOptions`,
  `Parse_SyncAddAzure_NoContainer_ReturnsError`) would therefore pass on first run if
  written after b — no RED possible.
- Fix: execute f → b → c → d → e. f's parse facts failed genuinely (azure subcommand
  missing → "Unrecognized command or argument 'azure'"); b's behavior tests then failed
  as "unhandled command: sync add azure" (exit 1). Report the reorder as a deviation
  with the reason.

## Driver tests vs guard tests (factory cluster)

- **Driver:** `Create_WithAzureSettings_ReturnsAzureBlobCloudStore` — failed against the
  old factory (returned S3CloudStore regardless of provider). This test drives the switch.
- **Guards (passed against old code — pin the NEW implementation):** missing connection
  string; provider=azure + s3 rows only; provider=s3 + full s3 settings; provider row
  only — all must keep returning NullCloudStore. A naive `Provider switch` without the
  `!IsConfigured` guard would crash `AzureBlobCloudStore`'s ctor (ArgumentException →
  untyped tool error) instead of returning NullCloudStore.
- **Pin that can never be RED:** `--sync-connection-string` appended to the
  `Parse_SecretFlagsNeverDeclared_ReturnError` list — the unknown-option defense already
  exists; the test regression-pins it against a future declaration of the secret.

## Tooling pitfall: patch-tool escape drift

`patch` refused with "Escape-drift detected" on C# collection-expression patterns
(`["sync", "add", "s3"]`): old_string/new_string carried backslash-escaped quotes the
file doesn't have. Re-issue with plain quotes (no `\"`) and it applies.

## Gate recipe (per cluster and final)

- Per cluster: `dotnet test --filter "FullyQualifiedName~ClassA|ClassB|ClassC"` — verify
  RED fails for the right reason (feature missing, not a typo), then GREEN after the
  minimal implementation, then commit.
- Final: same filter green + `dotnet build` → "Build succeeded. 0 Warning(s) 0
  Error(s)" (warnings-as-errors), `git status` clean.
- Do NOT run the full suite when the task says targeted-only.
