# System.CommandLine 2.0.10 — subcommand options & value validation (verified 2026-08-06 by decompiling the package DLL)

Source: `ilspycmd -t <Type> ~/.nuget/packages/system.commandline/2.0.10/lib/netstandard2.0/System.CommandLine.dll`
(and `strings` greps). These facts were verified while reviewing a design that added a
`serve` subcommand with its own `--port` alongside the root's `--port` and a
`--idle-timeout` value-validated option.

## No ParseArgument hook in GA — Option.Validators is the native validation hook

- `Option<T>` public ctor: `Option(string name, params string[] aliases)` →
  `this(name, aliases, new Argument<T>(name))`. `Argument<T>` public ctor:
  `Argument(string name)`. Zero `ParseArgument` members exist in the whole assembly (grep of decompiled members). The beta's `parseArgument` delegate was removed.
- Consequence: `new Option<string>("--idle-timeout")` accepts ANY string with 0 parse errors. "SCL's normal error path will render it" is false unless you wire
  `Option.Validators` (`List<Action<OptionResult>>` — set `optionResult.ErrorMessage`), which makes the parse fail with the standard error rendering.

## Option<TimeSpan> EXISTS (but rejects unit-suffixed sugar)

- `ArgumentConverter.StringConverters` includes `[typeof(TimeSpan)] = … TimeSpan.TryParse …`. So `new Option<TimeSpan>("--idle-timeout")` compiles and parses `"4:00:00"`, `"1.5:30"`.
- `TimeSpan.TryParse("4h")` / `("90s")` / `("1d")` → false. Unit-suffixed sugar still needs a custom parser; justify it with the FORMAT, not with "SCL has no TimeSpan type".

## Same alias at root AND subcommand — name-based reads prefer the ROOT

- `ParseResult.GetResult(string)` → `SymbolResultTree.GetResult(name)`: builds
  `_symbolsByName` by DFS from the root (root's options BEFORE subcommand options), returns the FIRST symbol in that chain that has a result in the current parse.
- `--port 3 serve --port 5` → `GetResult("--port")` returns the ROOT's result (value 3). Name-based `GetValue<string>("--port")` is therefore WRONG for subcommand-scoped reads whenever both could be present. Use instance-based lookup
  (`ParseResult.GetValue<T>(Option<T> instance)` — the serve command's own Option instance) so "the verb's own --port wins" holds deterministically.

## Symbol.AddParent does NOT throw — multi-parent is silently chained

- `Symbol.AddParent(Symbol)` (decompiled): `if (FirstParent == null) FirstParent = new
  SymbolNode(symbol); else append to the linked list`. `Command.Add(Option)` performs no validation. The common belief "an Option instance can only be attached to one command — the second Add throws" is FALSE for 2.0.10.
- Reusing ONE static `Option` instance across repeated `BuildFullRootCommand()` calls (which test classes do constantly, in parallel under xunit v3) silently chains parents. Parse-time lookup is by instance identity against the current
  tree, so it works — but the static instance is shared mutable state across parses (theoretical race under concurrent tree builds) and a future SCL enforcing single-parent would throw at build.
- Guidance: per-parse instances when name-based reads suffice; keep shared static instances only when instance identity is required (the root/subcommand same-alias case) and comment why on the field.
