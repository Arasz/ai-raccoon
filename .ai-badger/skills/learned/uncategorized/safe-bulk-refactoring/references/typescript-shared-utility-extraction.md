# TypeScript/React: Extracting Shared Utilities

When the same constant, type, or helper function is duplicated across 2+ files, extract it to a shared module. This pattern applies to `Record` maps, label dictionaries, format helpers, validation schemas, and any pure value that multiple
components import.

## Workflow

### Step 1: Read all consumers in parallel

Before writing any code, read every file that contains the duplicated definition. This avoids mid-task surprises (e.g., one consumer uses `Record<string, string>` while another uses `Record<SpecificUnion, string>` — the extraction should
tighten to the stricter type).

### Step 2: Create the shared module

Create the new file with the export. Prefer the stricter type:

```typescript
// salary-format.ts
import type { SalaryPeriod } from "@/offers/types";

export const SALARY_PERIOD_UNIT: Record<SalaryPeriod, string> = {
  hourly: "hour",
  daily: "day",
  monthly: "month",
  yearly: "year",
};
```

### Step 3: Update each consumer — import + remove local def

In each consumer file, do TWO things:

1. Add the import: `import { SALARY_PERIOD_UNIT } from "@/offers/salary-format";`
2. Remove the local `const SALARY_PERIOD_UNIT` definition

**LSP conflict between patches is expected.** When the import is added before the local definition is removed, the LSP reports `Import declaration conflicts with local declaration`
(TypeScript error 2440). This is transient — the second patch (removing the local def)
resolves it. Don't let the diagnostic stop you; apply both patches.

### Step 4: Check for type-tightening side effects

When the extracted type is stricter than the original (e.g., `Record<string, string>` →
`Record<SalaryPeriod, string>`), TypeScript may now report that nullish coalescing or optional chaining on the lookup is unnecessary — the key union covers all cases.

These are **pre-existing lint issues** that the looser type hid. Report them but don't fix them in the extraction PR unless the user asks — they're a separate concern.

### Step 5: Verify

Run the relevant test suites for all consumer files:

```bash
npx vitest run <test-paths-for-all-consumers>
```

If the project has lint, run it too — the type change may surface new warnings.

## Pitfalls

- **Don't remove an import that's still used elsewhere.** Before removing `import type { JobOffer }`
  from a consumer, check whether other code in that file still references `JobOffer`. The constant may have been the only thing using the type in one file but not another.

- **`Record<string, string>` vs `Record<Union, string>` matters for exhaustiveness.** If the original was `Record<string, string>`, switching to `Record<Union, string>` is a type improvement but may cause `TSC` errors if any consumer
  accesses the record with a plain
  `string` key (not the union). Check all call sites.

- **Naming: put the file next to its primary domain.** A salary format helper belongs in
  `offers/salary-format.ts`, not `lib/` or `utils/`. Co-locate with the types it imports.
