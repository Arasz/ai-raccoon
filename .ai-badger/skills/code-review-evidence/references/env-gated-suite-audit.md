7. **Env-gated suites: prove the pinning, audit precedent parity.** Features
   gated behind an env var (default OFF) and hook/plugin features that fire on
   tool calls or inject into LLM turns have a recurring evidence shape:
   - **Empirical env-pinning check.** Re-run the new test files with the
     ambient env var exported to its ON value (`AI_BADGER_MEMORY_GRADE=1
     pytest tests/test_feature_*.py`). If the OFF-path tests still pass, their
     `monkeypatch.delenv/setenv` pinning is real — the suite will not inherit a
     machine-wide enable. This matters because the feature plan often enables
     the var machine-wide for dogfooding on the author's host, so ambient ON
     is the realistic condition the suite must survive. One command converts
     "the tests say they pin the env" into verified fact; report the run in
     the review.
   - **Precedent-parity audit.** A feature explicitly built as a mirror of an
     existing precedent (same lazy sibling-import, stash/pop, hook entry) must
     have a test for every behavior the precedent's suite pins. Diff the two
     suites test-by-test; the highest-value gap is the inert-without-sibling
     path on BOTH callbacks (`_load_x()` → None when the sibling script is
     absent, e.g. older scaffold). Then check whether the per-turn injection
     callback calls the loader OUTSIDE try/except: a loader that raises there
     breaks every LLM turn on every host, and the suite never catches it if
     every test injects the sibling via sys.modules.
   - **Transport-specific stash pollution.** Shared logic that stashes pending
     state for a transport with no pop (a PostToolUse hook returning
     additionalContext directly while the shared function also writes the
     other agent's pending file) leaves unpopped asks a later session on the
     OTHER agent pops. A plan-vs-code deviation even when benign — flag it.
   - **Exit-0-always contracts.** A hook documented "advisory only, exit 0"
     must have its disk IO internally guarded (try/except inside the shared
     module), or an unwritable log dir escapes as traceback + exit 1 on the
     unguarded transport. Compare the precedent module's IO guards against
     the new one.
   - **Adversarial sweep.** env garbage/0/unset → fully off with zero IO;
     two events before one turn → last-wins; unknown pointer (ts) → exit 1
     with no mutation; absent optional arg → null (flag "" vs null
     inconsistencies); non-string tool names never match. Worked ai-badger
     hook review (29-test suite, all gaps found):
     `references/ai-badger-hook-feature-review.md`.
