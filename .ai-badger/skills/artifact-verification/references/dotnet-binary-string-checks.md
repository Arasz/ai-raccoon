# Verifying string copy in compiled .NET assemblies

Session origin: P4 of the azure-blob-sync plan (worktree task) — verified a
provider-neutral copy change to `MemoryTools.cs` (`[Description]` + two
`McpException` messages) that had no canonical gate (E2E asserts `IsError` only).
The v1 verification script FAILED on the Description check while the exception
checks passed — investigation showed the script was wrong, not the code.

## The metadata fact

Roslyn serializes string data into two different heaps, in two different encodings:

| Where the string lives | Encoding | Heap |
|---|---|---|
| IL string literal in a method body (`new McpException("…")`, `$"…"`) | UTF-16LE (length-prefixed) | #US (user strings) |
| Custom-attribute string argument (`[Description("…")]`, `[McpServerTool(Name=…)]`) | UTF-8, SerString (compressed length + UTF-8 bytes) | #Blob |

Consequences:
- Searching a DLL for an attribute-argument string as UTF-16 → false negative.
- `strings` on macOS/Linux (ASCII/UTF-8) finds the attribute strings but NOT the
  IL literals — the reverse miss.
- A naive whole-file `data.decode('utf-16-le')` search is alignment-fragile; the
  raw-bytes `needle.encode('utf-16-le') in data` form is safe.
- The MCP C# SDK (ModelContextProtocol 2.x) has NO source generator — the
  `[Description]` attribute is compiled into the assembly like any other attribute
  and read at runtime. If a build-time consumer did strip it, the control check
  below is what catches that.

## Recipe (python3, raw byte scan — both encodings)

```python
data = open(dll_path, 'rb').read()

def utf16(s): return s.encode('utf-16-le')
def utf8(s): return s.encode('utf-8')

NEW_DESC = "…"   # full expected attribute string, byte-exact
OLD_DESC = "…"   # fragment of the old copy that must be GONE
NEW_EXC  = "…"   # full expected exception literal
OLD_EXC  = "…"   # old exception fragment that must be GONE

checks = [
    ("new Description present (utf8 blob)",  utf8(NEW_DESC)  in data, True),
    ("old Description absent (utf8 blob)",   utf8(OLD_DESC)  in data, False),
    ("new exception present (utf16 literal)", utf16(NEW_EXC) in data, True),
    ("old exception absent (utf16 literal)",  utf16(OLD_EXC) in data, False),
]
for name, found, expect in checks:
    if found != expect:
        print(f"FAIL: {name}")
```

## Positive control (the step that diagnosed the false negative)

Before trusting any "absent" result, prove the scan works on the same string
kind: pick an UNTOUCHED string of that kind from the same file (e.g. another
`[Description]` like "Reports entry count") and require it present in the
expected encoding. In the session: `'Reports entry count'` had 0 UTF-16 hits and
1 UTF-8 hit → attribute args are UTF-8 → the new Description just needed a UTF-8
search. The hex dump confirmed it: `T\x0e\x04Name\x0bmemory_sync …` followed by
the full description text in UTF-8 inside the attribute blob.

## Keep the checks symmetric

For a copy replacement, assert BOTH directions per encoding: new string present
in its encoding AND old string absent in the same encoding. Presence-only or
absence-only checks can both pass while the other direction is wrong.
