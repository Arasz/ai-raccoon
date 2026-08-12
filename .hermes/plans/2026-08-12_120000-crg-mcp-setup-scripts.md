# Code-Review-Graph MCP Prerequisite Scripts, Setup Cleanup & Smart .venv Stack Detection Plan

> **For Hermes:** Use subagent-driven-development or follow task-by-task execution with plan review first.

**Goal:**

1. Replace manual skill setup notes for `code-review-graph` with executable prerequisite check and full venv installation scripts inside `features/common/mcp/code-review-graph/scripts/`, update `meta.json`, remove
   `features/common/skills/worktree-agent-isolation/references/code-review-graph-setup.md`, and clean up references.
2. Improve `python` stack detection in `detect.py`: inspect `.venv` contents against the project's declared/catalog MCP tool server dependencies so a `.venv` created solely for MCP tools (e.g. `code-review-graph`, `semantica`) in non-Python
   projects does NOT falsely trigger the `python` stack signal.
3. Open a PR to `ai-badger`.

**Current Context & Active Workstream:**

- `task/semantica-integration-part2` is currently in progress in `ai-badger` and is scheduled to be merged into `main` shortly (~5 minutes).
- Worktree creation for this task will sync/rebase against `origin/main` once `task/semantica-integration-part2` lands on `main`.

**Architecture:**

- **MCP Scripts:** Executable Python scripts in `features/common/mcp/code-review-graph/scripts/` (`check.py` and `install.py`). `meta.json` points `prerequisite.check` and `prerequisite.install` to these scripts. `install.py` manages Python
  3.10+ validation, virtual environment creation (`.venv`), and `code-review-graph` pip installation.
- **Smart `.venv` Detection:** `detect.py` checks `.venv` package distributions (`.dist-info`) against declared/catalog MCP tool packages (e.g. `code-review-graph`, `semantica`, and standard helper libraries like `mcp`, `pydantic`,
  `tree-sitter`). If `.venv` contains ONLY tool dependencies and no user Python project manifest (`pyproject.toml`, `requirements.txt`, `setup.py`, `setup.cfg`, `Pipfile`) exists, `python` stack is suppressed.

**Tech Stack:** Python 3 (cross-platform), `venv`, `pip`, `ai-badger` catalog tooling (`index_build.py`, `version_sync.py`, `validate.py`).

---

## Tasks

### Task 1: Worktree Setup after `semantica-integration-part2` Merge

**Objective:** Create feature worktree off updated `main` after `task/semantica-integration-part2` merges.

**Files:**

- Create directory: `/Users/arasz/RiderProjects/ai-badger-crg-mcp-setup`

**Step 1: Fetch and verify `origin/main` includes `semantica-integration-part2`**
Run: `cd /Users/arasz/RiderProjects/ai-badger && git fetch origin main`

**Step 2: Create worktree**
Run: `git worktree add ../ai-badger-crg-mcp-setup origin/main`
Expected: Worktree created at `../ai-badger-crg-mcp-setup`.

**Step 3: Create feature branch**
Run: `cd /Users/arasz/RiderProjects/ai-badger-crg-mcp-setup && git checkout -b feature/crg-mcp-prereq-scripts`
Expected: Switched to new branch `feature/crg-mcp-prereq-scripts`.

---

### Task 2: Create Prerequisite Check Script (`check.py`)

**Objective:** Create a cross-platform check script that verifies a Python >=3.10 environment and `code-review-graph` availability.

**Files:**

- Create: `features/common/mcp/code-review-graph/scripts/check.py`

**Step 1: Write `check.py`**
`check.py` will:

1. Check if `code-review-graph` CLI is executable on `PATH` or in local `.venv` (`.venv/bin/code-review-graph` or `.venv/Scripts/code-review-graph.exe`).
2. Verify Python version is >= 3.10.
3. Test `code-review-graph --version` or `python3 -c "import code_review_graph"`.
4. Exit 0 if ready, exit 1 if missing/incompatible.

**Step 2: Verify `check.py` execution**
Run: `python3 features/common/mcp/code-review-graph/scripts/check.py`
Expected: Returns 0 if installed or 1 if missing.

---

### Task 3: Create Full Venv Installation Script (`install.py`)

**Objective:** Create a cross-platform installation script that finds/creates a Python 3.10+ virtual environment and installs `code-review-graph`.

**Files:**

- Create: `features/common/mcp/code-review-graph/scripts/install.py`

**Step 1: Write `install.py`**
`install.py` will:

1. Discover Python 3.10+ interpreter (checking `sys.executable`, `/opt/homebrew/bin/python3.14`, `python3.12`, `python3.11`, `python3.10`, `python3`).
2. Create or verify a local `.venv` virtual environment if not in an active venv.
3. Run `pip install --upgrade code-review-graph` using the target venv's Python.
4. Verify `code-review-graph --version` works in the venv and exit 0.

**Step 2: Verify `install.py` execution**
Run: `python3 features/common/mcp/code-review-graph/scripts/install.py`

---

### Task 4: Update `meta.json` Prerequisite Commands

**Objective:** Point `features/common/mcp/code-review-graph/meta.json` `check` and `install` to the new Python scripts.

**Files:**

- Modify: `features/common/mcp/code-review-graph/meta.json`

**Step 1: Update `meta.json`**
Set:

- `prerequisite.summary`: `"Python 3.10+ virtual environment with code-review-graph installed"`
- `prerequisite.check`: `"python3 features/common/mcp/code-review-graph/scripts/check.py"`
- `prerequisite.install`: `"python3 features/common/mcp/code-review-graph/scripts/install.py"`

**Step 2: Validate schema**
Run: `python3 tooling/validate.py --all`
Expected: Schema validation passes.

---

### Task 5: Remove Skill Reference & Clean Up Skill Links

**Objective:** Delete `features/common/skills/worktree-agent-isolation/references/code-review-graph-setup.md` and remove references in skill docs.

**Files:**

- Delete: `features/common/skills/worktree-agent-isolation/references/code-review-graph-setup.md`
- Modify: `features/common/skills/worktree-agent-isolation/SKILL.md`
- Modify: `features/common/skills/worktree-agent-isolation/references/comparative-agent-experiments.md`

**Step 1: Delete file**
Run: `git rm features/common/skills/worktree-agent-isolation/references/code-review-graph-setup.md`

**Step 2: Remove references**
Remove line referencing `code-review-graph-setup.md` from `SKILL.md` and `comparative-agent-experiments.md`.

---

### Task 6: Implement Smart `.venv` Stack Detection in `detect.py`

**Objective:** Enhance `detect.py` so a `.venv` containing only MCP tool server packages does not trigger false-positive `python` stack detection on non-Python projects.

**Files:**

- Modify: `features/common/skills/welcome-ai-badger/scripts/detect.py`
- Modify: `.ai-badger/skills/welcome-ai-badger/scripts/scaffold.py` / `detect.py` copy (keep in sync)

**Step 1: Implement `_is_mcp_only_venv(target, index)` helper in `detect.py`**

1. Read catalog MCP server package names from `features/*/mcp/*/meta.json` (e.g. `code-review-graph`, `semantica`).
2. Add standard venv baseline packages (`pip`, `setuptools`, `wheel`, `_virtualenv*`, `distutils`, `mcp`, `pydantic`, `tree-sitter*`, `typing_extensions`, `annotated_types`, `htbuilder`, etc.).
3. Scan `.venv/lib/python*/site-packages/*.dist-info` (and `venv/`).
4. If `.venv` exists AND all packages are in the MCP tool set AND no user Python project manifest (`pyproject.toml`, `requirements.txt`, `setup.py`, `setup.cfg`, `Pipfile`) exists, treat `.venv` as an internal tool venv and exclude it from
   `python` stack detection.

**Step 2: Add unit tests in `tests/test_detect.py`**

- Test: `.venv` containing only `code-review-graph` / `mcp` packages in a `.NET` project does NOT detect `python` stack.
- Test: `.venv` containing user packages (e.g. `fastapi`, `pytest`, `requests`) OR presence of `pyproject.toml` DOES detect `python` stack.

---

### Task 7: Write Unit Tests & Run Quality Gates

**Objective:** Add TDD unit tests for MCP scripts and stack detection, sync catalog plugins, rebuild index, bump VERSION, add changelog, and run pre-push verification.

**Files:**

- Create: `tests/test_code_review_graph_mcp_scripts.py`
- Modify: `tests/test_detect.py`
- Modify: `VERSION`
- Create: `docs/changelog/<version>-code-review-graph-mcp-scripts.md`
- Modify: `docs/changelog/README.md`

**Step 1: Run pytest**
Run: `python3 -m pytest tests/test_code_review_graph_mcp_scripts.py tests/test_detect.py -v`

**Step 2: Sync plugin skills & index**
Run:
`python3 tooling/sync_plugin_skills.py`
`python3 tooling/index_build.py`
`python3 tooling/version_sync.py`

**Step 3: Run CI & Quality Gates**
Run:
`python3 -m pylint $(git ls-files '*.py' | grep -v '^tests/')`
`python3 tooling/validate.py --all`
`python3 -m pytest tests/ -q`

---

### Task 8: Commit & Submit Pull Request to `ai-badger`

**Objective:** Push feature branch and create pull request via `gh pr create`.

**Step 1: Commit changes**
`git add -A`
`git commit -m "feat(mcp): add venv check/install scripts for code-review-graph and smart .venv stack detection"`

**Step 2: Push and create PR**
`git push -u origin feature/crg-mcp-prereq-scripts`
`gh pr create --title "feat(mcp): add venv check/install scripts for code-review-graph and smart .venv stack detection" --body-file /tmp/pr-body.md`
