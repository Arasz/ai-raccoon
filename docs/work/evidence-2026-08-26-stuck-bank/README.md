# Evidence bundle — owner's bank, stuck-migration state

Captured 2026-08-26 ~13:35 CEST (read-only `cp -p` of the live bank + WAL + SHM), committed as
`docs/work/evidence-2026-08-26-stuck-bank/`. Purpose: the doctor lane's manual exit-24 row and the
P4 stale-lease tests need a fixture in the exact pre-remediation state (R3 §3.3 sequencing tension —
P4's relay change makes the live bank drain, destroying the fixture).

## State at capture (read from the copy, `mode=ro`)

- `model_migration` id=1: provider `local`, model `…/Salesforce__SFR-Embedding-Code-400M_R`,
  `started_at` 1787739481, `finished_at` NULL (**open**), `lease_owner`
  `MacBook-Air-Arasz:92861:6792f8b0c93a47b3961c9a8e2688f7c6`, `lease_expires_at` **1787744154**.
- `entries`: 51,947 total; **41,547 pending** at capture (down from 47,723 at 12:35 — the lease was
  renewed at ~13:35:54, i.e. **a server is draining the migration live right now**).
- settings: `embedding.provider=local`, `embedding.model` == `embedding.codeModel` == the code model
  dir, `embedding.codeDimensions=1024`, no `embedding.dimensions` row.

## SHA-256 (pin the copy)

- memory.db:   `e4bdf6e61257e4e9c3cf00684748de6521260e13e4169ce3adf4c88e60ee5775`
- memory.db-wal: `70d623eac40eb2ccf65616cf792d6794cf7a44e15cccdf83fc2e11011f6cda29`

## Caveats

- The WAL was captured ~2 min after the main db file; the bank is being written by a live drain.
  Use the copy as a structural fixture (migration row, settings, schema), not as a byte-exact
  replica of the 12:35 state.
- Do not commit any run that writes to this bundle; treat it as read-only evidence.
