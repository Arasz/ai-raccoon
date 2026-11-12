# ADR-0145: Target .NET 11 and compile with runtime-async

Date: 2026-11-12

Status: Proposed. The release version (D1), the ONNX Runtime scope (D2) and RollForward (D3) wait on the owner gate `docs/work/2026-11-12-dotnet-11-upgrade-review.html`.

## Context

.NET 11 shipped on 2026-11-10 as a Standard Term Support release, with SDK 11.0.100 and runtime 11.0.0. It ships C# 15 and Runtime Async. With Runtime Async, the runtime runs `async` methods directly, so the compiler does not emit a state machine for each one. It is a preview feature and is off by default for app code. A `net11.0` project turns it on with `<Features>runtime-async=on</Features>` and does not need `EnablePreviewFeatures`. NativeAOT and ReadyToRun support it. `new StackTrace()` shows fewer frames. Exception stack traces stay the same.

The solution targets `net10.0` and global.json pins SDK 10.0.401. The global tool is framework-dependent and has no roll-forward policy. On a machine whose .NET install has only the 11.0.0 runtime, the 2.6.1 tool does not start, and the MCP server is unavailable. 32 NuGet packages have newer versions. Most of them are the Microsoft.Extensions and Microsoft.* train moving from 10.x to 11.0.0.

## Decision

- Every `net10.0` project targets `net11.0`. The source generator stays on `netstandard2.0`. global.json pins SDK 11.0.100 with `latestPatch`. CI, scripts and script tests that name `bin/Debug/net10.0` or `tools/net10.0` follow the new folder.
- `Directory.Build.targets` turns on runtime-async for every `net11.0` project: `<Features>$(Features);runtime-async=on</Features>`. It is a `.targets` condition, because `TargetFramework` is not set yet when `Directory.Build.props` is read. A test checks that a production `async` method has the runtime-async method flag, so a build that loses the switch fails the test.
- Packages move to their latest stable versions, including System.CommandLine 3.0. Each major bump is a separate commit with its API changes. These packages stay where they are: `HiraokaHyperTools.sqlite-vec` (retrieval-harness rule), and `Microsoft.ML.Tokenizers` with `Microsoft.ML.Tokenizers.Data.O200kBase` at 2.0.1 (the owner deferred the 3.0 update on 2026-11-12). ONNX Runtime follows D2.

## Options

- Stay on `net10.0` and add only `RollForward`. This brings back the server on .NET 11 machines, but it keeps the old runtime train and does not give runtime-async.
- Move to .NET 11 without runtime-async. This is less risk, but the user asked for runtime-async. The switch is one property, so it is simple to turn off again.

## Consequences

- Users need the .NET 11 runtime. The README gets a Breaking changes line.
- Async stacks from profilers and `new StackTrace()` are shorter. Code that counts frames or reads the names of compiler-generated `MoveNext` frames must not depend on them.
- Thread-affine native calls (the MLX session, ADR for #722) keep their dedicated thread. Runtime-async changes how a continuation is run, not where `ConfigureAwait(false)` or a custom scheduler sends it. The MLX and CoreML tests are the check.
- If runtime-async causes a defect, remove the one property in `Directory.Build.targets` to roll back.
