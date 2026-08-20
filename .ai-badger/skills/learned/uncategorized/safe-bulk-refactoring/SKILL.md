---
name: safe-bulk-refactoring
description: "Safely update test files after domain-model refactoring: build-loop discipline, regex pitfalls, and targeted-replacement workflows."
version: 1.0.0
author: Hermes Agent
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [refactoring, test-update, csharp, dotnet, bulk-changes]
---

# Safe Bulk Refactoring

## Overview

When a domain-model refactoring changes API signatures (property renames, method parameter reordering, type renames), the test files need bulk updates. Doing this with regex scripts is fast but dangerous — a single over-broad replacement
can create hundreds of syntax errors and consume more time in cleanup than the refactoring saved.

**Core principle:** One pass, one replacement, one build. Never chain multiple regex passes without a build in between.

Domain-specific migration patterns (Durable Task APIs, NSubstitute mocks, step lifecycles) are catalogued in [`references/durable-task-migration-patterns.md`](references/durable-task-migration-patterns.md). TypeScript/React shared-utility
extraction (import + remove local def, LSP conflict resolution, type-tightening side effects) is in [`references/typescript-shared-utility-extraction.md`](references/typescript-shared-utility-extraction.md). The TDD workflow for
**extending** aggregates with new subdomains (records, delegation methods, schema contracts, serialization round-trips) is in [`references/csharp-domain-model-extension-tdd.md`](references/csharp-domain-model-extension-tdd.md). The phased
workflow for **removing an entire subsystem** and replacing it with a leaner alternative (create replacement → update consumers → delete old files → DI → infra → frontend → tests) is in [
`references/subsystem-removal-with-replacement.md`](references/subsystem-removal-with-replacement.md). The **ILogger constructor injection** pattern (add `ILogger<T>` param → update all test sites with `NullLogger<T>.Instance` → watch for
duplicate `using`) is in [`references/ilogger-constructor-injection-refactoring.md`](references/ilogger-constructor-injection-refactoring.md). A **repo-wide project/product identifier rename** (project name, namespaces, dirs, env vars, data
roots — `git mv` pitfalls, replacement ordering, case-insensitive FS, straggler detection) is in [`references/repo-wide-identifier-rename.md`](references/repo-wide-identifier-rename.md).

## When to Use

- Domain model properties moved to a nested object (e.g., `InterventionRequired` → `Intervention.IsRequired`)
- Factory method signatures reordered (e.g., `Create(id, type, ...)` → `Create(id, orchId, type, ...)`)
- Types renamed (e.g., `SaveFooInput` → `WorkflowContainerSaveInput<Foo>`)
- Method signatures gained required parameters

## The Safe Workflow

### Phase 1: Understand the target API shape

Read the production code to understand EXACTLY what changed:

- What properties were renamed/restructured?
- What methods gained new required parameters?
- What classes were renamed?
- What parameter order changed?

Write down the mapping before touching any test file.

### Phase 2: Build the feedback loop

```bash
dotnet build 2>&1 | grep -c "error CS"
```

This is your tight loop. Run it after EVERY change pass.

### Phase 3: Apply ONE change category at a time

Never apply property-access fixes AND parameter-order fixes AND type-rename fixes in the same regex pass. Each category gets its own pass with a build in between.

**Good:**

```python
# Pass 1: only .InterventionRequired → .Intervention.IsRequired
content = content.replace('.InterventionRequired', '.Intervention.IsRequired')

# Build, verify, THEN pass 2
```

**Bad:**

```python
# Everything at once — debugging failures means searching through all changes
content = content.replace('.Foo', '.Bar')
content = content.replace('.Baz', '.Qux')
content = re.sub(r'Create\((.*),(.*),(.*),(.*),(.*),(.*)\)', r'Create(\1,\5,\2,\3,\4,\6)', content)
# ... 10 more replacements
```

### Phase 4: Use `git checkout` as undo between passes

Each pass that goes wrong is easy to recover:

```bash
git checkout HEAD -- tests/
```

Restore, fix the script, re-run. This is infinitely faster than hand-editing 200 malformed lines.

## Triage Workflow: Fixing Test Failures After Production API Changes

When production code changed and tests fail, follow this order:

### Step 1: Identify the API change, not the test failure symptom

Do NOT read test output and jump to fixing assertions. First run `dotnet test` to get the full failure dump, then read the **production code** that changed. Understand the exact API surface:

- What method/endpoint now calls what?
- Are new extension methods involved? (e.g., `RaiseStepResolutionEvent` replacing direct `RaiseEventAsync`)
- Did parameter order change?
- Did NewGuid mock consumption change? (e.g., a phase now uses `Guid.CreateVersion7()` instead of `context.NewGuid()`)

### Step 2: Cluster failures by root cause

The same API change often breaks multiple tests. Group failures by root cause, not by test file:

| Symptom                                                     | Root Cause                                                                            |
|-------------------------------------------------------------|---------------------------------------------------------------------------------------|
| Assertion expects `Action.Retry` but receives `Action.Skip` | Endpoint now calls `RaiseStepResolutionEvent()` which always sends `Skip`             |
| "Discarded 100 resolution events"                           | Mock `WaitForExternalEvent` returns wrong step ID because NewGuid consumption shifted |
| Intervention.IsRequired expected `True` but got `False`     | `AddOrReplaceStep` now calls `ClearInterventionIfNoAutomaticStepAwaitsUser`           |
| Status expected `AnalysisFailed` but got `Analyzing`        | `TryMarkOrchestrationAsFailedAsync` only sets intervention flag, not status           |

### Step 3: Apply per-cluster fixes, build after each cluster

Fix all tests in one root-cause cluster together, build, verify, then move to the next cluster.

### Step 4: When `GetAliveInstancesAsync` is in play, set up `GetAllInstancesAsync` mocks

If production code calls `GetAliveInstancesAsync()` (extension on `DurableTaskClient`), the test must set up `GetAllInstancesAsync` to return instances:

```csharp
using Azure;  // for Page<T> and AsyncPageable<T>

var metadata = new OrchestrationMetadata(nameof(SomeOrchestration), "instance-1") 
    { RuntimeStatus = OrchestrationRuntimeStatus.Running };
var page = Page<OrchestrationMetadata>.FromValues([metadata], null, Substitute.For<Response>());
durableTaskClient.GetAllInstancesAsync(Arg.Any<OrchestrationQuery>())
    .Returns(AsyncPageable<OrchestrationMetadata>.FromPages([page]));
```

**Without this setup**, `GetAliveInstancesAsync()` returns empty and downstream methods like `ResetJobOfferAnalyze` are never called, leaving step/container status unchanged.

## Critical Pitfalls

### 1. NEVER deduplicate `using` statements with a naive line-based approach

In C#, `using var buffer = new MemoryStream();` is a local variable declaration, NOT a using directive. A script that finds duplicate `using `-prefixed lines will remove these declarations, creating
`CS0103: The name 'buffer' does not exist` errors.

**Safe dedup:** Only deduplicate lines that match `using NamespaceName;` — check for a semicolon at the end and no `var` keyword:

```python
if line.strip().startswith('using ') and not 'var ' in line and line.strip().endswith(';'):
    # This is a using directive — safe to deduplicate
```

### 2. Global `replaceAll` in Rider/JetBrains tools can be too broad

`replace_text_in_file` with `replaceAll=true` replaces ALL occurrences. If you want to replace only one specific occurrence, provide enough surrounding context to make it unique (3-5 lines of context minimum).

### 3. Regex argument reordering breaks on nested delimiters

C# factory methods can have arguments with embedded commas (e.g., `JsonNode.Parse("""{ "key": "value" }""")`). A simple `([^,]+)` regex will split on the commas inside JSON strings. These calls need manual fixing.

### 4. `init` vs `private init` on record properties

If a property has `private init`, test code in a different assembly CANNOT set it via `with` expressions. The fix is either:

- Change `private init` to `init` on the production code (tests can then use `with { Prop = value }`)
- Use domain methods exclusively in tests (e.g., `RequireIntervention()` instead of `with { Intervention = ... }`)

The second approach preserves encapsulation but makes test setup more complex for edge-case states.

### 6. Tool masking corrupts `patch` edits on sensitive content

When `read_file`, `terminal`, or other tools **mask/redact sensitive values** (API keys, passwords, tokens), the `patch` tool's fuzzy matching can silently replace the real value in the file with the masked version.

**How it happens:**

1. `read_file` shows `apiKey: "***"` or `apiKey: "«redacted:sk-…»"`
2. You write a `patch` targeting nearby context lines
3. The patch tool matches the original file content but your replacement text carries the masked value forward
4. The file now has the masked string instead of the real one → mysterious test failures ("expected `sk-or-123456` but got `«redacted:sk-…»`")

**Workaround — use `terminal` with Python to edit the file directly:**

```bash
cd /path/to/project && python3 << 'PYEOF'
with open("path/to/file", "r") as f:
    content = f.read()
# Make targeted replacements on safe context only
content = content.replace("safe surrounding context", "new surrounding context")
with open("path/to/file", "w") as f:
    f.write(content)
PYEOF
```

This preserves original sensitive values because you never expose them through a tool that masks them.

**Verification:** After any edit near sensitive values, run `git diff <file>` and confirm only intended lines changed. If you see an API key or token value changed, revert with `git checkout <file>` and redo with the Python workaround.

### 7. Wrapping existing UI in an accordion/disclosure breaks tests

When you wrap existing UI elements (inputs, labels, buttons) in a collapsible component (accordion, disclosure, tab), **every test that queries those elements by label or role will fail** because Radix UI accordions remove collapsed content
from the DOM entirely (not just `display: none`).

**Required test updates (in order):**

1. **Replace "no disclosure exists" tests** with "collapsed by default" tests:
   ```ts
   const trigger = screen.getByRole("button", { name: /trigger text/i });
   expect(trigger).toHaveAttribute("aria-expanded", "false");
   expect(screen.queryByLabelText(/wrapped field/i)).not.toBeInTheDocument();
   ```
2. **Add a "reveals on expand" test:**
   ```ts
   await user.click(trigger);
   expect(trigger).toHaveAttribute("aria-expanded", "true");
   expect(screen.getByLabelText(/wrapped field/i)).toBeInTheDocument();
   ```
3. **Update every existing test that interacts with wrapped content** to expand the accordion first:
   ```ts
   // Before interacting with fields inside the accordion:
   await user.click(screen.getByRole("button", { name: /trigger text/i }));
   // Now the fields are accessible:
   await user.type(screen.getByLabelText(/field name/i), "value");
   ```

**Radix UI specifics:** `Accordion` with `type="single" collapsible` defaults to all items closed. Content is removed from DOM when closed, so `getByLabelText()` throws (use `queryByLabelText()` for the collapsed assertion).

### 8. `execute_code` `read_file` + `write_file` silently truncates large files

When using `hermes_tools.read_file()` inside `execute_code`, the default limit is 500 lines. For files exceeding this limit, `read_file` returns truncated content **without error**. If you then pass that truncated content to `write_file()`,
the file is permanently shortened — all content beyond line 500 is destroyed.

**This happened in production:** 5 C# test files (1000+ lines each) were silently truncated to 500 lines. Recovery required `git checkout` to restore the originals.

**Safe patterns:**
| Intent | Tool | Why | |--------|------|-----| | Read + edit targeted lines | `read_file` with explicit `offset`/`limit`, then `patch` | Patch does in-place replacement, never overwrites | | Bulk find-and-replace across files | `patch`
tool with `mode='replace'` | Operates on live file content, never truncates | | Programmatic multi-file edits in execute_code | `search_files` to locate, then `patch` to edit | Avoids reading/writing entire file contents |

**Dangerous pattern — NEVER do this:**

```python
# In execute_code:
content = read_file(path)           # truncated at 500 lines!
new_content = modify(content)
write_file(path, new_content)       # overwrites with truncated content
```

**When you must read entire files in `execute_code`:** Check `result["total_lines"]` against the returned line count. If they differ, the file was truncated — do NOT write it back.

### 9. C# `using var` + never-completing Task is incompatible with NSubstitute mocks

When production code wraps a `Task` in `using var`:

```csharp
using var timerTask = context.CreateTimer(deadline, ct);
```

`Task.Dispose()` throws `InvalidOperationException` ("A task may only be disposed if it is in a completion state") if the task has not completed. This means you **cannot** mock `CreateTimer` to return a never-completing task
(`new TaskCompletionSource<Task>().Task`) as a default in a
`NewContext()` helper — the `using` block will crash when the method exits.

**The trap:** The natural fix for "Task.WhenAny picks the wrong task" is to make the losing task never-complete, so the other always wins. But if production code uses `using var` on that task, disposal throws.

**Correct alternatives:**

1. **Cancel the timer after event wins:** The production code should cancel the timer's
   `CancellationToken` after `Task.WhenAny` returns the event, transitioning the timer to Canceled (a completable, disposable state). Ensure this `await cts.CancelAsync()` exists in the production code path.
2. **Remove `using var` from Task:** If the Task doesn't hold unmanaged resources, `using`
   is unnecessary. The Durable Task Framework manages timer lifecycle internally.
3. **Track and complete the TCS in test cleanup:** Set up CreateTimer with a lambda that captures a `TaskCompletionSource`, then complete it (SetCanceled/SetResult) in a finally block or after the orchestration call.

**Also relevant:** `Task.WhenAny(timerTask, eventTask)` where both tasks are already completed returns the first one in the argument list deterministically. If `timerTask` is listed first and both are `Task.CompletedTask`, the timer always
wins — the event path is never taken.

### 10. The `patch` tool silently skips matches when multiple patches target the same file

When issuing multiple `patch` commands against the same file in one call, later patches can silently fail if earlier patches shifted line numbers. The patcher fuzzy-matches against the original file content, but when patch #1 changes lines
20-30 and patch #2 targets lines 70-80 that now sit at a different offset, patch #2 may report "no occurrences found" without warning.

**Always verify each patch was applied** by reading the affected lines after a bulk patch call. When the patch tool misses, fall back to Rider's `mcp__rider__replace_text_in_file` for exact string matching — it operates on the live file and
handles quoting correctly.

### 11. Duplicate `replace_text_in_file` hits — ensure uniqueness with extra context

When two similar-looking patterns exist in a file (e.g., two SendCv test methods with nearly identical assertion blocks), a `replace_text_in_file` call may match the wrong instance or both. Add 3-5 lines of surrounding context (including
test method names or comments) to disambiguate.

### 12. Grep for construction sites before bulk-updating tests for a signature change

When a plan or PR description says "tests X and Y construct the record and WILL break on the new ctor param", VERIFY with grep first (`TypeName\(` across src + tests). Only files that actually construct the type break (CS7036/CS1729);
callers that go through a factory/resolver keep compiling. Plans routinely overestimate breakage — one session was told two test files needed ctor updates, but a single grep showed only the production resolver constructs the record: the
tests needed assertion extensions, not rewrites. Update the tests that DO break, extend the rest with assertions for the new behavior, and never delete coverage while doing so.

## Recommended Tool Stack

| Operation                     | Tool                                               | Why                                                    |
|-------------------------------|----------------------------------------------------|--------------------------------------------------------|
| Find all occurrences          | Rider MCP `search_in_files_by_text`                | Fast, structured output                                |
| Targeted line replacements    | Rider MCP `replace_text_in_file`                   | Handles C# string quoting correctly                    |
| Simple property renames       | Python `content.replace()` via `execute_code`      | Fast for bulk `.Foo` → `.Bar` changes                  |
| Complex regex transformations | Avoid if possible; use targeted Rider replacements | Regex over-application is the #1 source of regressions |
| Undo after bad pass           | `git checkout HEAD -- tests/`                      | Instant recovery                                       |

## Example: Intervention Refactoring Pass Order

When `InterventionRequired`/`InterventionReason`/`InterventionSource` flat properties moved into a nested `Intervention` record:

1. **Pass 1:** Property access `.InterventionRequired` → `.Intervention.IsRequired` (simple `.replace`)
2. **Pass 2:** Property access `.InterventionReason` → `.Intervention.Reason`
3. **Pass 3:** Property access `.InterventionSource` → `.Intervention.Source`
4. **Pass 4:** Fix filter/model classes that should NOT change (e.g., `ApplicationListFilter.InterventionRequired` stays)
5. **Pass 5:** `with` expression conversion (manual or targeted per-line)
6. **Pass 6:** Factory method reordering (`CreateAutomatic`/`CreateManual`)
7. **Pass 7:** Type renames (`SaveActivityInput` → `WorkflowContainerSaveInput<T>`)
8. **Pass 8:** New required parameters on method calls

Build after every pass. Restore from git if any pass introduces >10 new errors.
