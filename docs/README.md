# Documentation

The canonical documentation tree for AiRaccoon. Docs root: `docs/`.

## Map

| Directory | Purpose |
|---|---|
| `tutorials/` | Learning-oriented walkthroughs — we choose the goal, the reader follows |
| `how-to/` | Task-oriented recipes — the reader has a goal, we show the steps |
| `reference/` | Information-oriented lookups — consulted mid-task. Currently: [agent-memory-server.md](reference/agent-memory-server.md) (tool contract) |
| `explanation/` | Understanding-oriented background — why it is like this. Currently: [agent-memory-architecture.md](explanation/agent-memory-architecture.md) |
| `adr/` | Architecture decision records — immutable, frozen |
| `work/` | Dated work records: plans, designs, research, reviews, incidents, backlog |
| `assets/` | Images and diagrams for the documentation |
| `meta/` | Machine state: ledger, indexes, baselines |
| `features/` | Feature dossiers + Gherkin contracts, indexed by [features/README.md](features/README.md). Currently: agent-memory (Implemented) |

## Conventions

- Filenames: kebab-case, `.md`, no capitals (except `README.md`). Grammar per quadrant:
  `tutorials/` and `how-to/` start with an imperative verb (`build-the-server.md`),
  `reference/` uses a bare noun (`tool-contract.md`), `explanation/` a noun phrase or
  `why-` (`why-http-is-optional.md`), `work/` is dated `YYYY-MM-DD-slug`.
- A directory's `README.md` is a complete map: every file in it, one line of purpose each.
- Depth cap: three path segments below `docs/` (four under `work/`).
- Frozen paths (build-embedded content, paths pinned by config or agent instructions,
  everything under `adr/`) are never moved, renamed, or reformatted.
- The docs tree has no ledger yet — no `CHANGELOG.md` until a ledger exists to generate it.
