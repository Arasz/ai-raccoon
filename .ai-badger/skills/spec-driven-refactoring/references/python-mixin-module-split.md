# Python Mixin-Based Module Split

When a single Python file grows too large (500+ lines) with multiple distinct domains, split
it into domain-specific modules using a **mixin pattern** that preserves the public API.

## When to use

- Single file with 3+ independent domains (e.g. MCP management, hook wiring, template rendering)
- The file has a main class that owns all the methods
- Tests import the module dynamically (via `importlib.util.spec_from_file_location`)
- Multiple copies of the file exist (source + scaffolded)

## Pattern

### 1. Identify domain boundaries

Use `code-review-graph`'s `find_large_functions_tool` and `query_graph_tool` (file_summary) to
map every function in the file. Group by domain:
- Functions that share the same data (e.g. all read from `mcp-servers.json`) → same module
- Functions with no cross-dependencies → separate modules
- Entry point / CLI → stays in the original file

### 2. Create mixin modules

Each domain gets its own file exporting a mixin class:

```
mcp_tools.py        → class McpToolsMixin:       (MCP server + external tool management)
hook_wiring.py      → class HookWiringMixin:     (hook wiring into settings files)
template_rendering.py → class TemplateRenderingMixin: (doc slots, template render)
agent_files.py      → class AgentFilesMixin:     (scaffolding.json application)
extensions.py       → class ExtensionsMixin:     (extension parsing, merge, embed)
```

The original file becomes the composition point:
```python
from mcp_tools import McpToolsMixin
from hook_wiring import HookWiringMixin
# ...

class Scaffolder(McpToolsMixin, HookWiringMixin, TemplateRenderingMixin, ...):
    # Only __init__, core orchestration, and non-extracted methods remain
    pass
```

### 3. Handle dynamic script loading

When tests load scripts via `importlib.util.spec_from_file_location`, the script's directory
is NOT automatically on `sys.path`. Add this BEFORE the mixin imports:

```python
_SCRIPT_DIR = str(Path(__file__).resolve().parent)
if _SCRIPT_DIR not in sys.path:
    sys.path.insert(0, _SCRIPT_DIR)
```

### 4. Handle circular imports with lazy imports

Mixin modules that need symbols from the main module (constants, utility functions) should
import them lazily inside methods, not at module top:

```python
def _prune_inline_extensions(self, skill_name, dest):
    from scaffold import requirement_met  # lazy to avoid circular import
    # ...
```

### 5. Keep copies in sync

When the same file exists in multiple locations (source directory + scaffolded output),
sync ALL copies after every change:
```bash
for f in scaffold.py mcp_tools.py hook_wiring.py ...; do
    cp features/common/scripts/$f .ai-badger/skills/.../scripts/$f
done
```

## Pitfalls

- **Pyright reports unknown attributes on mixin classes.** This is expected — mixins access
  `self.root`, `self.config`, `self.notes`, etc. that exist on the composing class. The errors
  resolve at runtime. Don't add dummy attributes to the mixin just to silence the linter.

- **Pylint reports cyclic-import for mixin → main module lazy imports.** Expected for this
  pattern. Suppress with `# pylint: disable=cyclic-import` at module level or accept the warning.

- **`_test_ignore` (shutil.ignore_patterns) must stay in the main module or be duplicated.**
  It's a module-level constant used by multiple mixins. Either import it lazily from the main
  module or define it in a shared constants module.

- **Standalone functions used by tests must remain importable from the original module.**
  If tests do `scaffold.merge_hooks(...)` or `scaffold.cfg_get(...)`, keep those in scaffold.py
  or re-export them: `from hook_wiring import merge_hooks`.

- **Mixin method docstrings should reference the domain, not "this class."** Since mixins are
  composed, "this class" is ambiguous. Write "Collect stack-declared MCP servers" not
  "This method collects..."

## Verification checklist

After splitting:
1. Run the full test suite — all tests should pass unchanged
2. Run pylint on all new + modified files
3. Run the project's build/check command
4. Verify that `load_script("path/to/scaffold.py")` still works in tests
5. Check that both source and scaffolded copies are identical
