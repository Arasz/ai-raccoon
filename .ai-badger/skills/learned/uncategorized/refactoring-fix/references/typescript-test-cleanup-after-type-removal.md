# TypeScript Test Cleanup After Type/Constant Removal

When exported types, constants, interfaces, or functions are removed from a TypeScript module, test files that reference them break at compile time. This pattern covers the safe cleanup workflow.

## When to Use

- An exported symbol (type, constant, interface, function) was removed from a module
- Test files compile/lint against the old API surface
- The removed symbols may be used in multiple test files with different roles (imports, mock data, assertions, guards)

## The Safe Workflow

### Step 1: Read the source module first

Before touching any test file, read the **production module** that changed. Understand EXACTLY what was removed:

```bash
# Example: what was removed from problem-types.ts?
grep -n 'export' src/app/api/problem-types.ts
```

Build a mapping:
- What constants/properties were removed?
- What interfaces/types were removed?
- What functions were removed?
- What remains that test code might still need?

### Step 2: Search all test files for every removed symbol

Use a combined regex to find ALL references at once:

```bash
grep -rn --include='*.ts' --include='*.tsx' \
  -E 'symbol1|symbol2|symbol3' app/ || true
```

This prevents missed references. A single stale `ProblemType.removedProperty` in an unrelated test file will cause a compile error at lint or CI time.

### Step 3: Classify each reference before editing

For each test file with references, classify every usage of the removed symbol:

| Usage Role | Action |
|---|---|
| **Import** — only used for removed symbols | Remove import line entirely |
| **Import** — also used for surviving symbols | Keep import, remove only the removed symbol from the destructured list |
| **Mock/test data constant** — entire object built around removed symbol | Remove the entire constant + all tests that use it |
| **Assertion** — `expect(x).toBe(RemovedSymbol)` | Replace with a surviving symbol that tests the same contract |
| **Type guard** — `isProblemOfType(err, RemovedType)` | Replace with a different type URI that tests the same guard behavior |
| **Test case** — entire `it()` block exists solely to test removed behavior | Remove the entire test block |
| **Describe block** — entire `describe()` exists solely for removed feature | Remove the entire describe block |

**Critical rule:** Never leave a stale reference. If `budgetProblem` constant uses `ProblemType.llmBudgetExceeded` and is also used in OTHER tests (e.g., `stackedAnalysisRun`'s "returns undefined for other problem" test), you must either:
- Replace the constant's definition with a valid type, OR
- Replace each usage with inline valid test data, OR
- Remove all tests that depend on the constant

### Step 4: Fix one file at a time, verify each

```
For each affected file:
  1. Make targeted edits (use `patch` tool, NOT full-file rewrite)
  2. Run the file's tests: `npx vitest run path/to/file.test.ts`
  3. Confirm 0 failures before moving to the next file
```

### Step 5: Final sweep

After all files are fixed:

```bash
# Verify zero stale references remain
grep -rn --include='*.ts' --include='*.tsx' \
  -E 'removed1|removed2|removed3' app/ && echo "FAIL" || echo "PASS"

# Run all three affected files together
npx vitest run file1.test.ts file2.test.ts file3.test.ts
```

## Common Patterns

### Pattern A: Remove entire test block

When a test only exists to exercise removed functionality:

```typescript
// REMOVE this entire block — the function it tests no longer exists
describe("llmBudgetFigures", () => {
  it("returns the exact ledger figures", () => { ... });
  it("never exposes estimatedCallCostUsd", () => { ... });
});
```

### Pattern B: Replace type in existing test

When the test's LOGIC is still valid but needs a different type URI:

```typescript
// BEFORE
expect(isProblemOfType(error, ProblemType.removedThing)).toBe(false);

// AFTER — same assertion shape, different (valid) type
expect(isProblemOfType(error, ProblemType.stillExists)).toBe(false);
```

### Pattern C: Replace mock data constant

When a mock data constant references removed types but is used in surviving tests:

```typescript
// BEFORE — references removed ProblemType.llmBudgetExceeded
const budgetProblem = {
  status: 429,
  type: ProblemType.llmBudgetExceeded,  // REMOVED
  title: "Llm Budget Exceeded",
};

// AFTER — replace with a valid type, keeping the test's intent
const otherProblem = {
  status: 409,
  type: "https://example.com/problems/step-pipeline-no-longer-running",
  title: "Step Pipeline No Longer Running",
};
```

### Pattern D: Keep import, trim removed symbol

```typescript
// BEFORE
import {
  isDeterministicRefusal,
  isProblemOfType,
  llmBudgetFigures,     // REMOVED
  ProblemType,
  stackedAnalysisRun,
} from "@/api/problem-types";

// AFTER
import {
  isDeterministicRefusal,
  isProblemOfType,
  ProblemType,
  stackedAnalysisRun,
} from "@/api/problem-types";
```

### Pattern E: Preserving test coverage for surviving behavior

When removing budget-related tests, ensure the "stacked analysis" tests still cover the same behavioral contracts (deterministic refusal, type matching, URI pinning). Don't accidentally remove the last test for `isDeterministicRefusal` — just remove the budget-flavored instance.

## Pitfalls

### 1. A removed symbol may be used as test data in unrelated tests

The `budgetProblem` constant might be used in `stackedAnalysisRun`'s "returns undefined for other problem" test. Removing only the `llmBudgetFigures` tests while keeping `budgetProblem` creates a dangling reference. Always grep for the constant name, not just the removed type.

### 2. Don't remove the import if other symbols from it are still used

If `ProblemType` is imported and used for both `llmBudgetExceeded` (removed) and `offerAnalysisInProgress` (still exists), keep the import but remove the specific removed property usages.

### 3. `vitest run` with a glob may pick up the wrong file

Prefer explicit paths: `npx vitest run app/api/problem-types.test.ts` over `npx vitest run **/*.test.ts`. The glob may match files in `node_modules` or other unexpected locations.

### 4. Pre-existing test failures in the full suite are noise

When running `bun run test` for the entire project, other test files may already be failing for unrelated reasons. Focus on the files you edited — run them individually to confirm YOUR changes pass. Report pre-existing failures separately.
