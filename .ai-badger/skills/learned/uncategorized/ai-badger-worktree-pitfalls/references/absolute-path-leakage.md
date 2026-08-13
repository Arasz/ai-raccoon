# The Absolute Path Leakage Trap

When operating within a `task` subagent isolated in a worktree (e.g., `.ai-badger/worktrees/task-name`), you must rigorously ensure all commands and file operations target that specific worktree directory.

**The Leak:**
If you hardcode absolute paths to the main repository root (e.g., `/Users/arasz/RiderProjects/ai-raccoon/src/...`) in your `patch`, `write_file`, or custom Python scripts, your changes will bypass the worktree isolation. This causes your
work-in-progress edits to "leak" directly onto the `main` branch (or whatever the primary checkout is currently tracking), polluting the main working directory and violating the isolation guarantees.

**The Fix:**

1. **Always use relative paths** starting with `./` when you have `cd`'d into the worktree.
2. If you must use absolute paths in scripts, derive them dynamically from the current working directory (`pwd` or `Path.cwd()`) assuming your terminal is already situated inside the worktree.
3. Double-check the target paths in your scripts before execution.
