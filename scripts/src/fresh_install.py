"""Pure helpers for the manual fresh-install test: file sha256 and MCP result unwrapping."""

import json


def sha(p):
    import hashlib
    h = hashlib.sha256()
    with open(p, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def unwrap(result):
    """MCP SDK wraps tool results as content[0].text containing a JSON string."""
    content = (result or {}).get("content") or []
    if content and content[0].get("type") == "text":
        return json.loads(content[0]["text"])
    return result
