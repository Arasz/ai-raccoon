# Tool registration surface tests — recipe + evidence

## Why class-reflection inventory tests are not enough

The `WatchToolsInventoryTests` / `ToolInventoryTests` pattern (ai-raccoon) reflects over
the tool CLASS:

```csharp
var tools = typeof(WatchTools)
    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
    .Where(a => a is not null)
    .Select(a => a!.Name)
    .ToList();
tools.Count.ShouldBe(3);
```

This asserts the class's attribute surface. Registration (`WithTools<T>()` in the host
setup) is separate and untested by it — the class can carry all 3 tools while the server
registers none.

## Incident evidence (ai-raccoon, 2026-08-06)

- `b358515` (watch feature): `McpServerSetup.cs` registered `.WithTools<MemoryTools>()` +
  `.WithTools<WatchTools>()` in the stdio host path.
- `01f0f63` (PR #30, host-paths refactor, shipped as 1.0.6): removed `.WithTools<WatchTools>()`
  from BOTH host paths (`CreateAppHost` + `ConfigureMcpServer` extension) as an incidental
  refactor edit.
- `WatchToolsInventoryTests` (class reflection) + `McpServerSetupHostTests` (transport
  shape only) both stayed green. Full suite 1107/0/43 passed.
- Live probe of the published 1.0.6 binary: `tools/list` → 16 tools, watch trio missing.
- Docs/prompts/README still advertised 19 tools. WatchPipeline/IWatchService DI was
  unaffected — only the MCP tool registration vanished.

## The stdio tools/list probe (full handshake required)

A bare `tools/list` frame over stdio returns NOTHING — the server hasn't negotiated a
session (no initialize). Sequence that works (Python, subprocess):

```python
import json, subprocess

proc = subprocess.Popen(['/path/to/server'], stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)

def send(method, params=None, mid=1):
    msg = {'jsonrpc':'2.0','id':mid,'method':method}
    if params: msg['params'] = params
    proc.stdin.write(json.dumps(msg) + '\n'); proc.stdin.flush()
    return json.loads(proc.stdout.readline())

send('initialize', {'protocolVersion':'2024-11-05','capabilities':{},
                    'clientInfo':{'name':'probe','version':'1'}}, 1)
send('notifications/initialized', None, 2)
tl = send('tools/list', None, 3)
names = sorted(t['name'] for t in tl['result']['tools'])
print(len(names), names)
proc.kill()
```

Notes:
- Keep stdin open — never close it before the response; a stdio server exits on stdin EOF.
- Responses are newline-delimited JSON-RPC frames.
- The HTTP/SSE variant needs `Accept: application/json, text/event-stream` and SSE-envelope
  unwrapping (see ai-raccoon-pitfalls skill for the wire format).

## Quick negative filter

```bash
DLL=$(find ~/.dotnet/tools/.store/arasz.ai-raccoon -name "AiRaccoon.dll" | head -1)
strings "$DLL" | grep -o "memory_watch_[a-z]*" | sort -u
```

Tool-name strings present in the binary ≠ registered. Strings absent = definitely not
registered. Only the host test or live probe settles it.
