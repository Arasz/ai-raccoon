# Project-ID Repair — Logic Flow (CI with a correct map)

From user request by CI to all project-ids fixed. Assumes the `--map` file is correct
(aliases → canonical winners, test residue in `Dropped`).

```mermaid
flowchart TD
    subgraph CI["CI / Operator"]
        A["1. Dry run<br/>`ai-raccoon repair project-ids --map ./map.json`"]
        B{"Map valid?<br/>file exists + JSON +<br/>non-null sections"}
        B_NO["Exit InvalidArgument<br/>`cannot load --map ...`"]
        V["8. Verify converged<br/>re-run dry run"]
        DONE["9. All project-ids fixed<br/>`summary — nothing to do`"]
    end

    subgraph CLI["CLI — ProjectIdsRepairCommands"]
        C["2. Load map bytes<br/>`ProjectIdAliasMap.FromJson`"]
        D["3. GET report<br/>`IRepairStore.ReportProjectIdsAsync`"]
        E["4. Plan locally<br/>`ProjectIdsFoldPlan.FromCensus`"]
        F["5. Print scoreboard<br/>folds / drops / retires /<br/>need-human / need-nothing<br/>+ per-fold + per-id lines<br/>+ `summary — repair needed`"]
        G["6. Apply<br/>`--apply`: POST report + mapJson<br/>`IRepairStore.RequestRepairAsync`<br/>print receipt + quiesce-or-rerun rule<br/>+ `summary — repair in progress`"]
    end

    subgraph SRV["Server — control plane"]
        H["GET /repair?kind=project-ids<br/>`RepairEndpoint` → `SqliteRepairStore`"]
        I["POST /repair<br/>validate mapJson →<br/>`repair_requests` outbox row"]
    end

    subgraph BANK["Bank — SQLite"]
        J["P1 census, SELECT-only<br/>`ProjectIdCensus.CollectAsync`<br/>clusters / orphans /<br/>zero-entry rows per id"]
        K["Outbox row<br/>`MemorySql.RequestRepair`"]
    end

    subgraph JOB["Maintenance poll ~15s — ProjectIdsRepairJob"]
        L{"Open request?"}
        L_NO["No-op<br/>return false"]
        M["Resolve stored map<br/>null/empty → Empty map<br/>invalid → warn 710, stay open"]
        N["Live census + fresh plan<br/>never replays CLI plan"]
        O{"Plan empty?"}
        P["`ProjectIdsRepair.ApplyAsync`<br/>one BEGIN IMMEDIATE txn per step:<br/>invalidate vec + code-vec → pending<br/>fold queue merge max-score/min-created<br/>fold entries move project-scope non-NULL<br/>dedup deletes no tombstone<br/>dropped deletes + tombstone per hash<br/>fold code / discards / quality<br/>fold watches preserve lease<br/>fold 5 id-keyed settings prefixes<br/>fold projects ensure winner,<br/>delete loser+dropped+retired<br/>rewrite tombstone PKs to winner"]
        Q["`ChunkIndexRepair`<br/>re-derive chunk positions<br/>under winner groups"]
        R["Mark finished<br/>`FinishRepairRequest`<br/>renamed rows pending for<br/>`PendingEmbedJob` / `CodeReindexJob` drain"]
    end

    A --> B
    B -- no --> B_NO
    B -- yes --> C --> D
    D --> H --> J --> D
    J --> E --> F
    F --> G
    G --> I --> K
    K --> L
    L -- no --> L_NO
    L -- yes --> M --> N --> O
    O -- yes, nothing to fold --> Q
    O -- no, folds/drops/retires --> P --> Q --> R --> V
    V -->|"folds/drops/retires = 0"| DONE
    V -->|"concurrent writer re-created loser<br/>(single-pass rule)"| A
```

## Step table

| # | Who | What | Code |
|---|-----|------|------|
| 1 | CI | Dry run with the correct map | `repair project-ids --map ./map.json` |
| 2 | CLI | Read map bytes once — dry-run plan and `--apply` forward identical bytes (AC3 identity) | `ProjectIdsRepairCommands.RunAsync`, `ProjectIdAliasMap.FromJson` |
| 3 | CLI → Server | Read-only report, CLI never opens the bank | `ServerSettingsStore.ReportProjectIdsAsync` → `GET /repair?kind=project-ids` |
| 4 | Server | SELECT-only census; report path skips the migration ladder so it never waits on a write lock | `SqliteRepairStore.ReportProjectIdsAsync` → `ProjectIdCensus.CollectAsync` |
| 5 | CLI | Derive work order: folds, dropped, retired, unresolved (needs human), needs-nothing | `ProjectIdsFoldPlan.FromCensus` |
| 6 | CLI | Print scoreboard + per-fold owner/NULL-context counts + per-id unattributed list + closing `summary — repair needed` | `ProjectIdsRepairCommands` |
| 7 | CI | Re-run with `--apply` once the plan looks right | `repair project-ids --map ./map.json --apply` |
| 8 | CLI → Server | POST the request with the same mapJson; receipt states poll cadence (~15s) and the single-pass quiesce-or-rerun rule | `ServerSettingsStore.RequestRepairAsync` → `POST /repair` |
| 9 | Server | Validate mapJson, write the `repair_requests` outbox row | `RepairEndpoint.MapPost` → `SqliteRepairStore.RequestRepairAsync` |
| 10 | Job | Next maintenance poll sees the open request (`HasWorkAsync`) | `ProjectIdsRepairJob.HasWorkAsync` |
| 11 | Job | Re-derive the plan from a **live** census with the **stored** map; invalid stored map refuses (stays open) | `ProjectIdsRepairJob.RunAsync` + `ResolveMap` |
| 12 | Job | Apply every id-keyed surface in per-step `BEGIN IMMEDIATE` transactions (failed step rolls back, request retries next pass) | `ProjectIdsRepair.ApplyAsync` |
| 13 | Job | Re-derive chunk positions under winner groups, mark request finished; renamed rows drain via the embed jobs | `ChunkIndexRepair`, `FinishRepairRequest`, `PendingEmbedJob` / `CodeReindexJob` |
| 14 | CI | Re-run dry run: `0 fold, 0 drop, 0 retire` + `summary — nothing to do` = fixed; a re-created loser means loop back to 1 | `repair project-ids` |

## Notes

- Plan buckets: **fold** (alias loser → winner), **drop** (test residue, tombstone per hash), **retire** (registered, empty), **unresolved** (a human must attribute — a correct map leaves none), **needs-nothing** (already canonical / empty).
- Scope rule: only `scope='project'` rows with non-NULL `context_label` move; NULL-context bulk rows stay loser-keyed by design (the per-fold line exposes their count); metrics / noise / workspaces / workspace scratch are never touched.
- Single-pass: a write under a folded id landing mid-apply re-creates the loser key — quiesce writers or loop derive → apply until no folds.
