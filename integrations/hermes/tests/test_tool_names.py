"""The plugin speaks the server's current tool names, derived from the server's own source.

2.0.0 renamed every MCP tool with no aliases (ADR-0140), so an old name is refused with
``unknown-tool``. Both name sets below are parsed from the C# source, never hand-copied.
"""

from __future__ import annotations

import ast
import pathlib
import re

REPO = pathlib.Path(__file__).resolve().parents[3]
TOOLS_DIR = REPO / "src" / "AiRaccoon" / "Tools"
PLUGIN_DIR = pathlib.Path(__file__).resolve().parent.parent / "ai-raccoon"
PLUGIN_SOURCES = ("client.py", "__init__.py", "status.py")


def _old_names() -> set[str]:
    text = (TOOLS_DIR / "RenamedToolNames.cs").read_text(encoding="utf-8")
    return set(re.findall(r'\["([a-z_]+)"\]\s*=\s*"[a-z_]+"', text))


def _live_names() -> set[str]:
    consts: dict[str, str] = {}
    used: list[str] = []
    for path in TOOLS_DIR.glob("*.cs"):
        text = path.read_text(encoding="utf-8")
        consts.update(re.findall(r'const string (Tn\w+)\s*=\s*"([^"]+)"', text))
        used += re.findall(r'McpServerTool\(Name\s*=\s*([^)]+)\)', text)
    return {consts[u] if u in consts else u.strip('"') for u in (x.strip() for x in used)}


def _literals(source: str) -> list[str]:
    """Every string literal outside docstrings (comments never reach the AST)."""
    tree = ast.parse(source)
    docstrings = {id(n.value) for n in ast.walk(tree)
                  if isinstance(n, ast.Expr) and isinstance(n.value, ast.Constant)}
    return [n.value for n in ast.walk(tree)
            if isinstance(n, ast.Constant) and isinstance(n.value, str) and id(n) not in docstrings]


def test_the_name_sets_parse_to_something():
    assert len(_old_names()) >= 25
    assert len(_live_names()) >= 40


def test_plugin_sources_use_no_pre_2x_tool_name():
    old = _old_names()
    assert len(old) >= 25
    pattern = re.compile(r"(?<!\w)(" + "|".join(sorted(old, key=len, reverse=True)) + r")(?!\w)")
    hits = []
    for name in PLUGIN_SOURCES:
        for literal in _literals((PLUGIN_DIR / name).read_text(encoding="utf-8")):
            hits += [f"{name}: {m.group(1)}" for m in pattern.finditer(literal)]
    assert hits == []


def _call_names(source: str) -> set[str]:
    return {n.args[0].value for n in ast.walk(ast.parse(source))
            if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute) and n.func.attr == "_call"
            and n.args and isinstance(n.args[0], ast.Constant)}


def test_client_calls_only_live_tool_names():
    live = _live_names()
    assert len(live) >= 40
    sent = _call_names((PLUGIN_DIR / "client.py").read_text(encoding="utf-8"))
    assert sent, "no _call(...) sites found"
    assert sent - live == set()


def test_dispatch_keys_and_schema_names_are_live_tool_names(provider_module):
    live = _live_names()
    assert len(live) >= 40
    assert set(provider_module._TOOL_DISPATCH) - live == set()
    assert {s["name"] for s in provider_module.TOOL_SCHEMAS} - live == set()


def test_search_sends_the_session_id_the_query_tool_requires(client_module):
    sent = []

    class Recorder(client_module._MCPClient):
        def _call(self, name, args):
            sent.append((name, args))

    Recorder().search("p", "q", session_id="sess-1")
    assert sent[0][0] == "query"
    assert sent[0][1]["sessionId"] == "sess-1"
