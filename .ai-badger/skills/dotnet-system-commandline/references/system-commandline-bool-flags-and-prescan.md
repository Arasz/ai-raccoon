# System.CommandLine bool flags, the verb pre-scan, and the fallback-record trap

Verified 2026-08-06 on System.CommandLine 2.0.10 GA (the project PR #65, server `--quiet` flag).

## Bool-flag read idiom

`Option<bool>` has arity ZeroOrOne: bare `--quiet` → true, `--quiet=false` → false.

```csharp
Quiet = parseResult.GetResult("--quiet") is OptionResult r ? r.GetValueOrDefault<bool>() : false
```

- No `Tokens.Count > 0` presence guard needed — the default (false) IS the absent case, unlike value options (`--port`
  needs the presence guard to distinguish "not given" from the default).
- Pin all three cases in tests: `--quiet` (true), absent (false), `--quiet=false` (false). The `=false` form is the one everyone forgets.

## Zero-arity bool options break "skip --opt + its value" verb pre-scans

The two-root pattern ("root with children requires a verb" → re-parse flag-only sets against a launch-only root) uses a token pre-scan to decide whether a verb is present:

```csharp
if (token.StartsWith("--")) { if (!token.Contains('=')) i++; continue; }
```

This assumes every option consumes exactly one value token. The first `Option<bool>` on the launch root violates it:
`--quiet access list` makes the pre-scan skip the verb
`access` as if it were the flag's value → "no verb found" → the launch-only root re-parse errors on the verb.

Impact: VALID invocations are unaffected (the full tree parses them before the fallback ever runs); only the error message for invalid invocations degrades. Still worth fixing:

- Fix A: walk the parsed `OptionResult`s and skip `result.Tokens.Count` tokens per option instead of assuming one.
- Fix B: special-case zero-arity options in the pre-scan.

## The fallback-record trap

`ReadOptions` wraps the option reads in `try { ... } catch (InvalidOperationException) {
return new() { defaults }; }` (invalid option values throw inside `GetValueOrDefault`). When a new option is added to the record, the fallback record must set it too — a bool property left off the fallback silently resets to false on a
conversion error on ANY other option. Same class of bug as a defaults record missing a new field.
