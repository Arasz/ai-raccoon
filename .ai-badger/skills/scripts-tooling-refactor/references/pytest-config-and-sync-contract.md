# pytest config + single-source contract pins (the project scripts-refactor 2026-08-06)

## Root pyproject.toml pytest wiring (validated)

Config-only root `pyproject.toml`, zero dependencies:

```toml
[project]
name = "the project-scripts"
version = "0.1.0"
requires-python = ">=3.9"

[tool.pytest.ini_options]
testpaths = ["scripts/tests"]
pythonpath = ["scripts/src"]
```

- `pythonpath` (pytest >= 7) puts `scripts/src/` on sys.path for tests — tests
  `import bundle` etc. with NO conftest and NO sys.path boilerplate.
- `python3 -m pytest scripts/tests` from the repo root picks the config up
  automatically; a bare `pytest --collect-only` also works (rootdir detection
  via the pyproject `[tool.pytest.ini_options]` table).
- `[project]` needs `version` to be strictly PEP 621-valid (name alone is not);
  nothing packages it, so the version is inert.
- Verified: P0 gate collected 0 tests cleanly with config present; full suite
  (79 tests across 3 parallel packages) ran from the root command.
- The dotnet side ignores the root pyproject.toml — no interference with the
  C# build.

## Single-source contract pins (kills the three-way manual sync)

Sibling scripts that shared pinned constants (model/vocab SHA-256s, RID lists)
used to require a manual "keep N files in sync" ritual — the sync check lived
in a docstring and failed loudly but late. The refactor consolidated:

- One `scripts/src/bundle.py` holds ALL pins + URLs (MODEL_NAME/MODEL_SHA256,
  VOCAB_*, GGUF_*).
- Every wrapper (verify-tool-package, manual-fresh-install-test,
  download-embedding-model) imports the pins instead of re-declaring them.
- `tests/test_bundle.py` asserts the exact constants — the test IS the sync
  contract; a pin change fails exactly one test.
- Verifying a port: grep the OLD script's constants (from `git show HEAD:<path>`)
  against the new module — byte-identical pins before trusting the port.

## Smoke commands that proved behavior without network/dotnet

- `python3 scripts/download-embedding-model.py bogus` → exit 2 (unknown-model
  path preserved from the .sh).
- `python3 scripts/patch-tool-shell.py` (no args) → usage output (same as the
  original's no-arg behavior).
- `python3 scripts/ingest-the reference repo-docs.py --chunk-only` → regenerated
  chunk-hash-map.json compared to the committed one: 772/772 entries, 0 diffs
  = hash contract byte-stable (the C# integration tests depend on it).
- Run the pytest gate on BOTH the dev python and the oldest supported system
  python to prove the syntax-compat claim (`/usr/bin/python3` = 3.9.6 on
  macOS CLT).
