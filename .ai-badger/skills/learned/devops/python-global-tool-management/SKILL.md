---
name: python-global-tool-management
description: >-
  Use when installing global Python CLI tools under PEP 668.
category: devops
---

# Global Python CLI Tool Management under PEP 668

Safely install and manage user-scoped Python CLI applications (e.g., `semantica`, `llm`, `ruff`, `black`) without violating PEP 668 (`EXTERNALLY-MANAGED`) or corrupting Homebrew/system site-packages.

## Trigger Conditions
Use when:
- Installing a global Python CLI tool or application on macOS or Linux.
- Hitting `error: externally-managed-environment` during `pip install`.
- Comparing or troubleshooting `uv tool`, `pipx`, or custom user virtual environments.

## Preferred Tool Installation Hierarchy

### 1. Primary Recommendation: `uv tool install <package>`
Use `uv tool install` as the default approach for user-scoped Python applications.

```bash
uv tool install <package>
```

**Why it outperforms alternatives:**
* **Automatic Python Runtime Compatibility**: If host Python (e.g. Homebrew Python 3.14) lacks pre-compiled binary wheels for C-extension dependencies, `uv` automatically downloads and uses a compatible standalone CPython build (e.g. 3.11 or 3.12).
* **Isolation**: Creates an isolated venv in `~/.local/share/uv/tools/<package>` and symlinks *only* application entry points into `~/.local/bin/`.
* **Clean Uninstallation**: `uv tool uninstall <package>` removes all symlinks and tool files cleanly.

### 2. Secondary Option: `pipx install <package>`
Use `pipx` when `uv` is not installed or when team conventions mandate `pipx`.

```bash
pipx install <package>
```

**Critical Pitfall with Host Python 3.14+:**
`pipx` defaults to the host Homebrew Python interpreter. If a package (or its dependency, e.g. `gensim==4.4.0`) lacks wheels for the latest host Python version, C-extension builds will fail.
* **Fix**: Override the Python version explicitly:
  ```bash
  pipx install --python python3.11 <package>
  ```

### 3. Advanced / Manual Option: Custom User Virtualenv
When third-party tool managers cannot be used:

```bash
uv venv ~/.local/venvs/<app> --python 3.11
uv pip install --python ~/.local/venvs/<app>/bin/python <package>
ln -sf ~/.local/venvs/<app>/bin/<app-binary> ~/.local/bin/
```

### 4. Rejected Anti-Patterns
* **`pip install --user`**: Blocked by PEP 668 under Homebrew Python to prevent `sys.path` pollution and ABI breaks across Python minor version upgrades.
* **`pip --break-system-packages`**: **DANGEROUS**. Corrupts Homebrew site-packages and leads to broken installations upon `brew upgrade`.

## Shell & PATH Integration
Ensure `~/.local/bin` is in `$PATH` in `~/.zshrc` / `~/.bashrc`:

```bash
export PATH="$HOME/.local/bin:$PATH"
```

Place `~/.local/bin` ahead of `/opt/homebrew/bin` so user-installed CLI tools take precedence while leaving Homebrew Python untouched.
