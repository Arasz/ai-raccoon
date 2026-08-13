# Empirical Benchmarks & Case Study: semantica v0.6.5 under PEP 668 on macOS

Tested on macOS Apple Silicon (macOS 26.6.1, Homebrew Python 3.14.6, Apple Python 3.9).

## Package Profile: semantica 0.6.5
* **Transitive Dependencies**: 135 packages (PyTorch, Gensim, SpaCy, OpenCV, SciPy, Transformers, Pandas, SymPy, etc.).
* **Exposed Applications**: 5 entry points (`semantica`, `semantica-explorer`, `semantica-mcp`, `semantica-server`, `semantica-worker`).

## Method Benchmark Results

### 1. `uv tool install semantica`
* **Status**: Success
* **Duration**: 17.76s
* **Runtime Behavior**: Detected that Homebrew Python 3.14 lacks pre-compiled wheels for `gensim==4.4.0`. Automatically downloaded and selected standalone `cpython-3.11-macos-aarch64-none`.
* **Symlink Footprint**: Symlinked exactly 5 entrypoints into `~/.local/bin/`. Dependency binaries (`ipython`, `spacy`, `torchrun`) were NOT exposed to PATH.

### 2. `pipx install semantica`
* **Status**: Failed out-of-the-box (Exit code 1).
* **Failure Mode**: `pipx` defaulted to Homebrew Python 3.14.6. Because `gensim==4.4.0` lacks CPython 3.14 wheels on PyPI, setuptools tried building C-extensions (`gensim/models/word2vec_inner.c`) against Python 3.14 header files, failing with C compiler errors (`no member named 'ma_version_tag' in 'PyDictObject'`).
* **Workaround**: Running `pipx install --python python3.11 semantica` resolved the issue.

### 3. `pip install --user semantica`
* **Status**: Blocked by PEP 668 (`error: externally-managed-environment`).
* **Enforcement File**: `/opt/homebrew/opt/python@3.14/Frameworks/Python.framework/Versions/3.14/lib/python3.14/EXTERNALLY-MANAGED`.
