# SCL 2.0.10: instance-based reads + option value validation (verified 2026-08-06)

Session: the project HTTP serve-mode WP1 (serve verb with `--port`, `--idle-timeout`,
`--mcp-entry`, `--format`). Every claim below was hit as a compile error or a failing
test against the real 2.0.10 package, then fixed and pinned green.

## 1. `ParseResult.GetValueForOption` does not exist in GA — it is `GetValue<T>(Option<T>)`

The pre-GA/beta name (`GetValueForOption`) appears in older posts and task briefs. Against
2.0.10 GA it is a compile error:

```
error CS1061: 'ParseResult' does not contain a definition for 'GetValueForOption'
```

The GA instance-based read is `ParseResult.GetValue<T>(Option<T>)` (plus
`GetRequiredValue<T>(Option<T>)` and `GetResult(Option)` for presence). Enumerated from
the package XML:

```
$ grep -o 'member name="[MTPS]:System.CommandLine.Parsing.ParseResult.[^"]*"' \
    ~/.nuget/packages/system.commandline/2.0.10/lib/net8.0/System.CommandLine.xml | sort -u
... GetRequiredValue``1(System.CommandLine.Option{``0})
... GetResult(System.CommandLine.Option)
... GetValue``1(System.CommandLine.Option{``0})
```

## 2. Duplicate aliases across commands are allowed; name-based reads prefer the ROOT

Root `--port` (launch identity) + subcommand `serve --port` coexist in one tree:
parses fine, `serve --help` renders its own `--port`. But `parseResult.GetResult("--port")`
resolves the ROOT option (DFS order) when both are given, so a runner reading the serve
value by name gets the root's. Fix: expose the serve options as
`internal static readonly Option<...>` fields on the tree class and read
`parseResult.GetValue(CliCommandTree.ServePortOption)` — instance-based, no name resolution.

Works with `DefaultValueFactory = _ => 7721` / `_ => "hermes"`: `GetValue(option)` returns
the default when the option is absent, the parsed token when present.

## 3. `Option.Validators` is the parse-error hook; errors go through `AddError`

`Option.Validators` (list of `ValidateSymbolResult<OptionResult>`) is the only SCL-native
parse-error hook in 2.0.10 GA — there is no `ParseArgument`. Setting a property named
`ErrorMessage` is a compile error:

```
error CS1061: 'OptionResult' does not contain a definition for 'ErrorMessage'
```

Working validator (custom span grammar on a string option):

```csharp
var option = new Option<string>("--idle-timeout") { Description = "...", HelpName = "span" };
option.Validators.Add(result =>
{
    var value = result.GetValueOrDefault<string>(); // null when the option is absent
    if (value is not null && !IdleTimeoutParser.TryParse(value, out _))
    {
        result.AddError($"Cannot parse argument '{value}' as an idle timeout: expected 90s/30m/4h/1d or 0 (disabled).");
    }
});
```

`AddError` is `SymbolResult.AddError(string)` (XML doc: "Adding an error will cause the
parser to indicate an error for the user and prevent invocation"). The parse error shows
up in `parseResult.Errors` and the render path exits 1.

## 4. TimeSpan parsing: no `4h` sugar, and From* overflow throws ArgumentOutOfRangeException

- SCL 2.0.10 HAS a TimeSpan converter, but it does not accept `4h`/`30m` sugar — keep the
  option as `Option<string>` + a pure static `TryParse(string?, out TimeSpan)`.
- `TimeSpan.FromDays(int.MaxValue-ish)` throws `ArgumentOutOfRangeException` — NOT
  `OverflowException` (observed: `System.ArgumentOutOfRangeException: TimeSpan overflowed
  because the duration is too long.` from `TimeSpan.FromDays`). A total `TryParse` must
  catch `ArgumentOutOfRangeException` (or bounds-check) so `"999999999d"` returns false.
  Pin it: add the overflow string to the invalid-input `[Theory]` rows.

## 5. TDD note: missing-API tests fail as BUILD errors, and that is valid RED

In C#, tests that reference not-yet-existing members (`CliCommandTree.ServePortOption`,
`IdleTimeoutParser`, a record's new positional param) fail the test-project build with
CS0234/CS1061 — that IS the observed RED for TDD; capture the CS codes as the RED
evidence, implement, then re-run for GREEN. The compile-error fix cycle is where the
API-name drift above surfaced.
