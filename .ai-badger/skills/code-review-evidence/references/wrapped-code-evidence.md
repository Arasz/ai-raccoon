1. **Identify what the code delegates to.** Any wrapper around a native
   extension, SDK, or external service is a candidate: the review must confirm
   the underlying semantics, not the wrapper's description of them.

2. **Verify semantics from the upstream source, not the spec/comments.**
   - Fetch the upstream implementation: `curl -sL
     https://raw.githubusercontent.com/<org>/<repo>/main/src/<file>` (or the
     pinned version tag — note `main` may differ from the pinned version).
   - Confirm the exact behaviors the wrapper depends on: dedup scope
     (per-context vs global), option persistence (connection-scoped vs stored
     in a settings table), argument requirements (NULL vs positive integer),
     delete granularity (per-context vs global by key), hidden filter columns
     on virtual tables.
   - A 3-line check of the upstream function is cheaper than a wrong verdict:
     this method found four real bugs in one review (see
     `references/sqlite-memory-semantics.md` for the verified facts and the
     exact functions to read).

2b. **When the data source is a store (SQLite/DB), query it directly.** A
   claim like "the parent row already aggregates children" or "the per-model
   table has one row per model" is a testable SQL fact, not a spec detail —
   open the real store read-only
   (`sqlite3.connect(f"file:{path}?mode=ro", uri=True)`) and check:
   `PRAGMA table_info` for the actual columns and PRIMARY KEY, `GROUP BY`
   cardinality counts (`SELECT session_id, model, COUNT(*) ... HAVING COUNT(*)
   > 1`), and a concrete arithmetic spot-check (parent row vs parent+child
   sum) before trusting the mapping in code or in a plan doc. "VERIFIED
   GROUND TRUTH" in a design doc means verified at *some* time by *somebody*
   — re-derive it. This caught two wrong claims in one review: a "parent
   aggregates include children" fact that was false 8/8 times measured, and a
   byModel mapping built on one-row-per-model when the real PK splits one
   model across 4 task rows (590 rows vs 367 distinct pairs).

2c. **When the wrapper maps SDK exceptions, verify the hierarchy and what the
   SDK actually throws.** A catch chain routing on SDK exception types
   (AWS/Azure/…) is only correct if the type graph of the RESTORED package
   versions matches the assumption. The compiler catches only the obvious
   failure — CS0160 fires when a later catch's type derives from an earlier
   catch's type in the same try — so a clean build proves nothing about the
   sibling direction: if you assume siblings and they are parent→child, the
   code compiles and the earlier catch silently swallows the later one's cases
   (errors misrouted to the wrong bucket). Probe: (a) build — CS0160-free is
   necessary but not sufficient; (b) reflection — throwaway console referencing
   the top-level package versions and printing `Type.BaseType` for every type
   the catches name (recipe + observed AWS/Azure facts in
   `references/dotnet-sdk-exception-probes.md`); (c) live — run the real client
   against a dead endpoint (port 9 refuses instantly, no network dependency)
   with and without credentials to learn what surfaces for no-creds vs
   connection-refused vs 401/403. Never trust a plan's "probe-verified" claim —
 re-verify.

 2d. **Prove test-isolation claims against the production code path.** When a
 suite claims it never touches the real store because a fixture sets an env
 var (`monkeypatch.setenv("APP_DATA_ROOT", tmp)`), verify the production code
 actually reads that var: `grep -rn "<VAR>" src/` — if the only hits are
 docs/tests/fixtures, the var is a no-op and the tests ran against the real
 store. Then confirm forensically instead of arguing: open the real store
 read-only and look for the suite's unique probe markers under a test-only
 project/key — exact probe strings in the real DB are the smoking gun (one
 review: all 4 probe writes from a "temp bank" suite found in the user's real
 `~/.<app>/memory.db` under a `hermes-itest` project; the CLI resolved
 its data root only from `--data-root`, defaulting to the user dir). The fix
 direction is usually passing the CLI arg through the spawn args
 (`StdioServerParameters(args=[...])`), not an env var the production path
 never reads. Also note the doc/README claims about the env var are then
 factually false and must be corrected with the code.
