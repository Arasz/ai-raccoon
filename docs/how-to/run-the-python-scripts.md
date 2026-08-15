# Run the Python scripts

`scripts/` holds standalone Python tooling that sits next to the .NET server: embedding-model
download/verification, the JSAA docs ingest pipeline, benchmark-corpus generation, the
structural-noise-model trainer, and a handful of one-off maintenance utilities. None of it ships
inside the packed tool; it runs from a checkout.

## Prerequisites

- Python 3.12 or newer (`pyproject.toml`'s `requires-python`) — `scikit-learn` and `numpy` need
  this at their current releases, which is why the floor isn't lower.
- [`uv`](https://docs.astral.sh/uv/). If it isn't already on your machine and
  `pip install --user uv` refuses with "externally-managed-environment" (PEP 668), install it in
  an isolated environment instead — `pipx install uv` or `brew install uv` both work without
  touching the system Python.

## Set up and run

```bash
uv sync                                    # creates .venv/, installs httpx/numpy/scikit-learn/pytest
uv run python3 scripts/<script-name>.py    # run any script through the managed venv
```

`uv sync` reads the pinned versions out of `uv.lock`, so everyone gets the same dependency graph.
Each script documents its own usage (arguments, what it does, any prerequisites specific to it) in
its module docstring — read that before running one. `scripts/train-structural-noise-model.py` is
the exception worth calling out up front: it needs an external corpus that is not checked into
this repository (see its docstring and `docs/adr/0041`), so it is not runnable from a bare
checkout.

## Run the test suite

```bash
uv run pytest scripts/tests
```

`scripts/tests/test_dependencies_declared.py` is a derived gate: it parses every `.py` file under
`scripts/` for third-party imports (via `ast`, so it can't be fooled by imports mentioned in
strings or comments) and every `[project.dependencies]` entry in `pyproject.toml`, and fails if an
import isn't declared. Add a new third-party import and this test tells you immediately instead of
someone discovering it only via `ModuleNotFoundError` on a machine that happened to have the
package installed globally.

**Python tests are not part of this repository's CI gate** (an explicit, previously recorded
owner decision) — this suite protects local runs and local development, not merges. A red test
here does not block a PR.

## See also

- [`scripts/tests/test_dependencies_declared.py`](../../scripts/tests/test_dependencies_declared.py)
- [ADR 0041 — structural noise detector](../adr/0041-structural-noise-detector.md)
