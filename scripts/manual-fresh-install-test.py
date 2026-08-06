#!/usr/bin/env python3
"""Manual fresh-install test for the ai-raccoon .NET global tool.

Proves a clean install from nuget.org works perfectly first try: all deps present,
no missing models, no silent repair. Post-publish complement to scripts/verify-tool-package.sh
(the pre-publish gate). Run from anywhere; everything happens in temp dirs and never touches
~/.dotnet/tools or ~/.ai-raccoon. Version override: AI_RACCOON_VERSION=1.0.8 (pin must be
bumped after each republish — NuGet versions are immutable).

Protocol (revised per architect plan review deleg_98028a5e):
  0. env preconditions: runtimes, uname, NUGET_PACKAGES isolated, unset AIRACCOON_DB_PASSPHRASE
  1. dotnet tool install --tool-path $TOOLPATH --version $V (fresh NUGET_PACKAGES -> real nuget.org fetch)
  2. layout check + sha256 of model/vocab (integrity, not presence)
  3. --version -> 2>&1, substring "$V"
  4. --help -> 2>&1, verb tree renders
  5. model set local (documented setup verb; bundled engine, no download)
  6. MCP stdio: initialize (<10s) -> serverInfo; notifications/initialized
  7. tools/call memory_write {projectId, content} unique string -> no error
  8. tools/call memory_search {projectId, query} -> written entry returns by hash (real vec0 path)
  9. tools/call memory_stats -> entries>=1 AND pending==0
 10. stderr assertions: no "Bundled embedding model unavailable", no "Downloading bundled model asset"
 11. dual-instance: 2nd server, fresh DATAROOT, concurrent initialize (5000-bind regression)
 12. graceful shutdown: close stdin -> exit 0
 13. zero-config probe: fresh DATAROOT, NO model set local, write+search -> record behavior
     (informational: FTS5-only by design — "model reset" prints 'no engine (FTS5-only search)')
 14. cleanup
Exit 0 = all green on first install attempt, zero manual repair.
"""
import json, os, re, shutil, subprocess, sys, tempfile, time, uuid

VERSION = os.environ.get("AI_RACCOON_VERSION", "1.0.8")
# NOTE: the model/vocab sha256 pins below are version-coupled and live in three places —
# this script, scripts/verify-tool-package.sh, and scripts/download-embedding-model.sh.
# After every model swap (or republish of a new bundle), re-derive the hashes from
# verify-tool-package.sh and sync all three files; a forgotten sync fails loudly here
# (sha mismatch = FAIL), never silently.

FAIL = []
def check(name, cond, detail=""):
    tag = "PASS" if cond else "FAIL"
    print(f"[{tag}] {name}" + (f" — {detail}" if detail else ""))
    if not cond:
        FAIL.append(name)

def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)

BASE = tempfile.mkdtemp(prefix="air-fresh-")
TOOLPATH = os.path.join(BASE, "tools")
DATAROOT = os.path.join(BASE, "data")
DATAROOT2 = os.path.join(BASE, "data2")
DATAROOT0 = os.path.join(BASE, "data0")  # zero-config probe
NUGET_PKGS = os.path.join(BASE, "nuget")
os.makedirs(TOOLPATH); os.makedirs(NUGET_PKGS)
PROJECT = f"manual-test-{int(time.time())}"
CONTENT = f"fresh install verification payload {PROJECT} — the quick brown raccoon vaults over the lazy badger"
import atexit
atexit.register(lambda: shutil.rmtree(BASE, ignore_errors=True))  # cleanup even on unexpected errors

print(f"== step 0: env preconditions (base {BASE}) ==")
r = run(["dotnet", "--list-runtimes"])
rts = r.stdout
check("net10 runtime present", "Microsoft.NETCore.App 10.0" in rts, rts.splitlines()[0] if rts else "none")
check("aspnet runtime present", "Microsoft.AspNetCore.App 10.0" in rts)
check("host is arm64", subprocess.run(["uname", "-m"], capture_output=True, text=True).stdout.strip() == "arm64")
had_passphrase = os.environ.pop("AIRACCOON_DB_PASSPHRASE", None)
os.environ["NUGET_PACKAGES"] = NUGET_PKGS
env = dict(os.environ)
check("no inherited AIRACCOON_DB_PASSPHRASE", had_passphrase is None, f"inherited={had_passphrase!r}")

print("== step 1: fresh install from nuget.org ==")
t0 = time.time()
r = run(["dotnet", "tool", "install", "--tool-path", TOOLPATH, "--version", VERSION, "ai-raccoon"], env=env, timeout=300)
install_ok = r.returncode == 0
check("dotnet tool install exits 0", install_ok, r.stderr.strip()[-300:] if not install_ok else "")
print(f"    install took {time.time()-t0:.1f}s")

print("== step 2: layout + integrity ==")
store_root = None
for dirpath, dirnames, filenames in os.walk(os.path.join(TOOLPATH, ".store")):
    if "tools" in dirnames:
        store_root = dirpath; break
check("store layout found", store_root is not None, TOOLPATH)
tool_dir = os.path.join(store_root, "tools", "net10.0", "osx-arm64") if store_root else ""
for f in ["AiRaccoon.dll", "AiRaccoon.deps.json", "AiRaccoon.runtimeconfig.json",
          "libonnxruntime.dylib", "libe_sqlite3.dylib", "libe_sqlite3mc.dylib", "vec0.dylib"]:
    check(f"present: {f}", os.path.isfile(os.path.join(tool_dir, f)))
model = os.path.join(tool_dir, "Models", "model_qint8_arm64.onnx")
vocab = os.path.join(tool_dir, "Models", "vocab.txt")
def sha(p):
    import hashlib
    h = hashlib.sha256()
    with open(p, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()
check("model sha256 pinned", sha(model) == "4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474", sha(model))
check("vocab sha256 pinned", sha(vocab) == "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3", sha(vocab))

print("== steps 3-4: --version / --help (stderr!) ==")
r = run([os.path.join(TOOLPATH, "ai-raccoon"), "--version"])
check(f"--version prints {VERSION}", VERSION in (r.stdout + r.stderr), f"stdout={r.stdout!r} stderr={r.stderr!r}")
r = run([os.path.join(TOOLPATH, "ai-raccoon"), "--help"])
help_text = r.stdout + r.stderr
check("--help renders verb tree", all(v in help_text for v in ["access", "model", "retrieval", "sync", "watch", "encryption", "--data-root"]), f"stdout={r.stdout!r}")

print("== step 5: model set local (bundled engine) ==")
r = run([os.path.join(TOOLPATH, "ai-raccoon"), "--data-root", DATAROOT, "model", "set", "local"])
check("model set local exits 0", r.returncode == 0, r.stderr[-300:])
check("model set local prints confirmation", "local" in (r.stdout + r.stderr).lower(), (r.stdout + r.stderr).strip())

class MCP:
    def __init__(self, tool, dataroot):
        self.proc = subprocess.Popen([tool, "--data-root", dataroot],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, text=True, bufsize=1)
        self.pending = {}
        self.id = 0
        self.stderr_lines = []
        self.reader = None
        import threading
        def drain():
            for line in self.proc.stderr:
                self.stderr_lines.append(line)
        self.reader = threading.Thread(target=drain, daemon=True)
        self.reader.start()
    def send(self, method, params=None):
        self.id += 1
        msg = {"jsonrpc": "2.0", "id": self.id, "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()
        return self.id
    def read_response(self, want_id, timeout=15):
        """Read stdout lines, match by id, skip notifications; every line must parse as JSON."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                raise TimeoutError("stdout closed before response")
            obj = json.loads(line)  # raises on log leakage
            if obj.get("id") == want_id:
                return obj
        raise TimeoutError(f"no response for id {want_id}")
    def close_stdin(self):
        self.proc.stdin.close()
    def wait(self, timeout=15):
        return self.proc.wait(timeout=timeout)
    def join_reader(self, timeout=5):
        if self.reader:
            self.reader.join(timeout=timeout)
        return "".join(self.stderr_lines)

print("== steps 6-9: MCP stdio round trip (fresh bank) ==")
mcp = MCP(os.path.join(TOOLPATH, "ai-raccoon"), DATAROOT)
t0 = time.time()
init_id = mcp.send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "manual-test", "version": "1.0"}})
init = mcp.read_response(init_id, timeout=15)
init_ms = (time.time() - t0) * 1000
check("initialize round-trip < 10s", init_ms < 10000, f"{init_ms:.0f}ms")
check("initialize no error", "error" not in init, json.dumps(init.get("error")) if "error" in init else "")
si = init.get("result", {}).get("serverInfo", {})
# serverInfo reports the assembly identity: name = assembly name, version = AssemblyVersion
# (numeric-only per csproj comment). The .mcp/server.json carries the marketing name/version.
check("serverInfo.name == AiRaccoon", si.get("name") == "AiRaccoon", str(si))
check(f"serverInfo.version starts {VERSION}", str(si.get("version", "")).startswith(VERSION), str(si))

def unwrap(result):
    """MCP SDK wraps tool results as content[0].text containing a JSON string."""
    content = (result or {}).get("content") or []
    if content and content[0].get("type") == "text":
        return json.loads(content[0]["text"])
    return result
mcp.send("notifications/initialized")

wid = mcp.send("tools/call", {"name": "memory_write", "arguments": {"projectId": PROJECT, "content": CONTENT}})
wres = mcp.read_response(wid, timeout=20)
check("memory_write no error", "error" not in wres, json.dumps(wres.get("error")) if "error" in wres else "")
wresult = unwrap(wres.get("result", {}))
check("memory_write returns entry (hash+context)", bool(wresult.get("hash")) and bool(wresult.get("context")), str(wresult)[:200])

sid = mcp.send("tools/call", {"name": "memory_search", "arguments": {"projectId": PROJECT, "query": "quick brown raccoon vaults lazy badger"}})
sres = mcp.read_response(sid, timeout=20)
check("memory_search no error", "error" not in sres, json.dumps(sres.get("error")) if "error" in sres else "")
search_text = json.dumps(unwrap(sres.get("result", {})))
# Identity check: the search result must carry the exact hash the write returned (ranking 1).
w_hash = wresult.get("hash", "")
check("memory_search returns the written entry (hash match)", w_hash and w_hash in search_text, search_text[:200])

stid = mcp.send("tools/call", {"name": "memory_stats", "arguments": {"projectId": PROJECT}})
stres = mcp.read_response(stid, timeout=15)
check("memory_stats no error", "error" not in stres)
stats = json.dumps(unwrap(stres.get("result", {})))
check("stats entries >= 1", re.search(r'"entries"\s*:\s*[1-9]', stats) is not None, stats[:200])
check("stats pending == 0", re.search(r'"pending"\s*:\s*0', stats) is not None, stats[:200])

print("== step 10: early stderr signal (no silent repair so far) ==")
time.sleep(1)
stderr_early = "".join(mcp.stderr_lines)
check("early: no 'Bundled embedding model unavailable'", "Bundled embedding model unavailable" not in stderr_early)
check("early: no 'Downloading bundled model asset'", "Downloading bundled model asset" not in stderr_early)
check("early: no 'Failed to download'", "Failed to download bundled model asset" not in stderr_early)

print("== step 11: dual-instance overlap (2nd server, fresh DATAROOT, 1st still alive) ==")
mcp2 = MCP(os.path.join(TOOLPATH, "ai-raccoon"), DATAROOT2)
i2 = mcp2.send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "manual-test-2", "version": "1.0"}})
init2 = mcp2.read_response(i2, timeout=15)
check("2nd instance initialize no error", "error" not in init2, json.dumps(init2.get("error")) if "error" in init2 else "")
si2 = init2.get("result", {}).get("serverInfo", {})
check("2nd instance serverInfo ok", si2.get("name") == "AiRaccoon" and str(si2.get("version", "")).startswith(VERSION), str(si2))

print("== step 12: graceful shutdown + final stderr (deterministic) ==")
mcp.close_stdin(); mcp2.close_stdin()
rc1 = mcp.wait(timeout=15); rc2 = mcp2.wait(timeout=15)
check("instance 1 exits 0 on stdin close", rc1 == 0, f"rc={rc1}")
check("instance 2 exits 0 on stdin close", rc2 == 0, f"rc={rc2}")
stderr_final = mcp.join_reader() + mcp2.join_reader()
check("final: no 'Bundled embedding model unavailable'", "Bundled embedding model unavailable" not in stderr_final)
check("final: no 'Downloading bundled model asset'", "Downloading bundled model asset" not in stderr_final)
check("final: no 'Failed to download'", "Failed to download bundled model asset" not in stderr_final)

print("== step 13: zero-config probe (informational) ==")
mcp0 = MCP(os.path.join(TOOLPATH, "ai-raccoon"), DATAROOT0)
i0 = mcp0.send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "probe", "version": "1.0"}})
mcp0.read_response(i0, timeout=15)
w0 = mcp0.send("tools/call", {"name": "memory_write", "arguments": {"projectId": PROJECT, "content": "zero-config probe content unique-xyz-9911"}})
mcp0.read_response(w0, timeout=15)
s0 = mcp0.send("tools/call", {"name": "memory_search", "arguments": {"projectId": PROJECT, "query": "unique-xyz-9911"}})
sr0 = mcp0.read_response(s0, timeout=15)
probe_hit = "zero-config probe content" in json.dumps(sr0.get("result", {}))
st0 = mcp0.send("tools/call", {"name": "memory_stats", "arguments": {"projectId": PROJECT}})
str0 = mcp0.read_response(st0, timeout=15)
probe_stats = json.dumps(str0.get("result", {}))
print(f"    zero-config: search hit={probe_hit}, stats={probe_stats[:160]}")
mcp0.close_stdin(); mcp0.wait(timeout=15)

print("== step 14: cleanup ==")
shutil.rmtree(BASE, ignore_errors=True)
print(f"cleaned {BASE}")

print()
if FAIL:
    print(f"RESULT: FAIL — {len(FAIL)} check(s) failed: {FAIL}")
    sys.exit(1)
print("RESULT: ALL GREEN — clean install works perfectly first try")
