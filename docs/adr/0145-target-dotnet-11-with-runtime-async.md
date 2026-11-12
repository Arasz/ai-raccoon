# ADR-0145: Target .NET 11 and compile with runtime-async

Date: 2026-11-12

Status: Accepted for 2.8.0.

## Context

.NET 11 shipped on 2026-11-10 as a Standard Term Support release, with SDK 11.0.100 and runtime 11.0.0. It ships C# 15 and Runtime Async. With Runtime Async, the runtime runs `async` methods directly, so the compiler does not emit a state machine for each one. It is a preview feature and is off by default for app code. A `net11.0` project turns it on with `<Features>runtime-async=on</Features>` and does not need `EnablePreviewFeatures`. NativeAOT and ReadyToRun support it. `new StackTrace()` shows fewer frames. Exception stack traces stay the same.

The solution targets `net10.0` and global.json pins SDK 10.0.401. The global tool is framework-dependent and has no roll-forward policy. On a machine whose .NET install has only the 11.0.0 runtime, the installed tool (2.6.1 on the owner's machine) does not start, and the MCP server is unavailable. 32 NuGet packages have newer versions. Most of them are the Microsoft.Extensions and Microsoft.* train moving from 10.x to 11.0.0.

## Decision

- Every `net10.0` project targets `net11.0`. The source generator stays on `netstandard2.0`. global.json pins SDK 11.0.100 with `latestPatch`. CI, scripts and script tests that name `bin/Debug/net10.0` or `tools/net10.0` follow the new folder.
- The release is a minor bump, 2.8.0. The tool keeps the default roll-forward policy; it does not set `RollForward`.
- `Directory.Build.targets` turns on runtime-async for every project whose target framework is `net11.0` or later: `<Features>$(Features);runtime-async=on</Features>`. It is a `.targets` condition, because `TargetFramework` is not set yet when `Directory.Build.props` is read. A test walks every product assembly and fails if an async method still compiles to a state machine, so a build that loses the switch fails the test.
- Packages move to their latest stable versions. System.CommandLine stays at 2.0.13 (#946): 3.0 compiles unchanged but changes the CLI's error and help output. These packages stay where they are: `HiraokaHyperTools.sqlite-vec` (retrieval-harness rule), `Microsoft.ML.Tokenizers` with `Microsoft.ML.Tokenizers.Data.O200kBase` at 2.0.1 (#943), and `Microsoft.ML.OnnxRuntime` at 1.30.0 (#944). The owner deferred the last two on 2026-11-12.

## Options

- Stay on `net10.0` and add only `RollForward`. This brings back the server on .NET 11 machines, but it keeps the old runtime train and does not give runtime-async.
- Set `RollForward=Major` on the tool as well. A runtime upgrade would then not stop the server, but the server could run on a runtime major it was not tested on. The owner rejected it on 2026-11-12.
- Release as 3.0.0 because the runtime requirement changes. The owner chose a minor bump on 2026-11-12.
- Move to .NET 11 without runtime-async. This is less risk, but the user asked for runtime-async. The switch is one property, so it is simple to turn off again.

## Consequences

- Users need the .NET 11 runtime to run 2.8.0. The README gets a Breaking changes line. A machine that has only a newer runtime major again stops the tool until the tool moves to that major.
- Async stacks from profilers and `new StackTrace()` are shorter. Code that counts frames or reads the names of compiler-generated `MoveNext` frames must not depend on them.
- Thread-affine native calls (the MLX session, ADR for #722) keep their dedicated thread. Runtime-async changes how a continuation is run, not where `ConfigureAwait(false)` or a custom scheduler sends it. The MLX and CoreML tests are the check.
- If runtime-async causes a defect, remove the one property in `Directory.Build.targets` to roll back. One project can opt out with `<UseRuntimeAsync>false</UseRuntimeAsync>`.
