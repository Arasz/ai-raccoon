# poc-parity fixtures (P1, plan §P1-1b / AC1.3)

Golden outputs for the default-off (`MMR_DISABLE=1`) post-fusion search path, captured on the
PRISTINE base build (commit `ae7a240a`, branch `task/air-threshold-filter-corpus-committee-eval`,
worktree `air-threshold-filter-corpus-committee-eval`) BEFORE the PoC port was applied. The
`scripts/tests/test_poc_port_gates.py::test_parity_vs_fixture_default_off` gate replays these
three queries and fails if the port perturbs the gated-off path.

## Bank copy (made by P1 via the sanctioned pattern, 2026-09-08)

- Module: `scripts/retrieval_tuning/make_memory_copy.py` — `run_copy_and_verify`
  (read-only URI source, SQLite `.backup`, integrity + count-parity + SHA-256 spot-check).
- Command: `uv run python scripts/retrieval_tuning/make_memory_copy.py --target
  /tmp/continue-testing-algorithm/datasets/memory-copy-p1-parity.db --sample-size 5`
- Copy path: `/tmp/continue-testing-algorithm/datasets/memory-copy-p1-parity.db`
- Copy sha256: `86be1ddac3db4741d1509bdee63565a7c71ef6c72a686005fc8e1d8b78ecc173`
- Live snapshot at copy time: `integrity_check=ok`, entries live=53792 copy=53792,
  embedded live=53792 copy=53792, 5/5 spot-checks matched.
- Server data root used for capture: `/tmp/ai-raccoon-eval-root-p1` (copy as `memory.db`;
  `models/` + `extensions/` symlinked from the live `~/.ai-raccoon` so the embedding model
  resolves; live logs deliberately not linked).

## Files

- `queries.json` — the three golden queries (2 × projectId `ai-raccoon`, 1 × `jsaa`), each
  carrying its own projectId (per-project scoping is load-bearing — research record §3).
- `base-goldens.json` — query id → `{memory: [...], code: [...]}` results. Timings stripped
  by construction; only deterministic fields kept: `hash`, `ranking`, `path`, `sourceFile`,
  `chunkIndex`, `snippet` (≤600 chars).

## Capture protocol

One server process per capture (`dotnet <dll> --data-root <root> --transport stdio`),
newline-delimited JSON-RPC: `initialize` → `notifications/initialized` → batched
`tools/call memory_search` with `sessionId="poc-parity-1"`. Env for capture and for the
parity replay: all `MMR_*` variables stripped, then `MMR_DISABLE=1`.
