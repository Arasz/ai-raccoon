# `dotnet build` stale error list after a cross-project rename

## Symptom

Renaming/deleting a type referenced by other projects (e.g. Infrastructure's
`TokenizerChunker` → `O200kTokenizer`) leaves ~25 `CS0246: <old type> not found` across test
files. But `dotnet build <test.csproj>` reports only 1 error (in a freshly-added file), hiding
the other 24.

## What did NOT force a full recompile (verified 2026-08-13, ai-raccoon)

- `dotnet build --no-incremental`
- `rm -rf src/*/obj/Debug src/*/bin/Debug tests/*/obj/Debug tests/*/bin/Debug`
- `dotnet build-server shutdown`
- `pkill -f VBCSCompiler`

The Roslyn compiler server (VBCSCompiler) kept serving a cached compilation for the unchanged
files, so the stale/partial error list survived every "clean" rebuild attempt.

## What DID force it

1. Adding a genuinely NEW source file — a probe like `new DefinitelyNotARealType123()`
   changes the compile input set and forces a full recompile. Delete the probe after.
2. `dotnet test` instead of `dotnet build` — the test runner forces a full build+run.

## Rule

When the error count looks implausibly small for the size of the rename, don't trust it —
force a full recompile before scoping, or you'll fix 1 error and discover the rest on the next
run. Cross-check the true blast radius by grepping the old symbol repo-wide
(`grep -rn "TokenizerChunker" tests/ --include="*.cs"`) rather than trusting the build's list.
