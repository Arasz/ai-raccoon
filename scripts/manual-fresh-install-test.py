#!/usr/bin/env python3
"""Manual fresh-install test for the ai-raccoon .NET global tool.

Proves a clean install from nuget.org works perfectly first try: all deps present,
no missing models, no silent repair. Post-publish complement to the pre-publish pack
gate; model/vocab sha256 pins come from scripts/src/bundle.py — the single source of
the bundle contract. Run from anywhere; everything happens in temp dirs and never touches
~/.dotnet/tools or ~/.ai-raccoon. Version override: AI_RACCOON_VERSION=1.6.0 (pin must be
bumped after each republish — NuGet versions are immutable). Source override:
AI_RACCOON_SOURCE=local installs from the repo's .nupkg-local (pre-publish dress
rehearsal of the same shell+payload nuget.org receives); default nuget fetches from
nuget.org. In local mode, first build the worktree with DOTNET_ENV=local (the
DeployToLocalSource target packs host-RID into .nupkg-local).

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
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))
from fresh_install import sha, unwrap
from bundle import MODEL_SHA256, VOCAB_SHA256

if "--help" in sys.argv or "-h" in sys.argv:
    print(__doc__)
    sys.exit(0)

VERSION = os.environ.get("AI_RACCOON_VERSION", "1.6.0")
SOURCE = os.environ.get("AI_RACCOON_SOURCE", "nuget")  # "nuget" | "local" (.nupkg-local)
LOCAL_SOURCE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".nupkg-local"))
# Model/vocab sha256 pins are imported from scripts/src/bundle.py — the single source
# of the bundle contract. Update bundle.py once; this script follows. A forgotten
# update fails loudly here (sha mismatch = FAIL), never silently.

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
# Fresh publish: the user-level NuGet http-cache can hold stale registration data
# (dotnet resolves via registration, which lags the blob) — clear it so a brand-new
# version is never false-FAILed by yesterday's cache.
http_cache = os.path.expanduser("~/.local/share/NuGet/http-cache")
shutil.rmtree(http_cache, ignore_errors=True)
env = dict(os.environ)
check("no inherited AIRACCOON_DB_PASSPHRASE", had_passphrase is None, f"inherited={had_passphrase!r}")

print("== step 1: fresh install" + (" from nuget.org" if SOURCE == "nuget" else " from .nupkg-local") + " ==")
t0 = time.time()
install_cmd = ["dotnet", "tool", "install", "--tool-path", TOOLPATH, "--version", VERSION, "ai-raccoon"]
if SOURCE == "local":
    if not os.path.isdir(LOCAL_SOURCE_DIR):
        print(f"FAIL: AI_RACCOON_SOURCE=local but {LOCAL_SOURCE_DIR} missing — build with DOTNET_ENV=local first", file=sys.stderr)
        sys.exit(1)
    install_cmd += ["--add-source", LOCAL_SOURCE_DIR]
r = run(install_cmd, env=env, timeout=300)
install_ok = r.returncode == 0
check("dotnet tool install exits 0", install_ok, r.stderr.strip()[-300:] if not install_ok else "")
print(f"    install took {time.time()-t0:.1f}s")
if not install_ok:
    # Everything below inspects the installed tool, so without it each step fails for the same
    # reason and the model sha256 check dies on a missing path — burying the real error under a
    # traceback. Stop here and let the install failure be the answer.
    print("\nFAIL: install failed; skipping the remaining steps (they all inspect the install).",
          file=sys.stderr)
    if "is not found in NuGet feeds" in r.stderr:
        print("Hint: a just-published version reaches api.nuget.org/v3-flatcontainer before the\n"
              "registration index that `dotnet tool install` queries. Check\n"
              "https://api.nuget.org/v3/registration5-gz-semver2/ai-raccoon/index.json and retry.",
              file=sys.stderr)
    sys.exit(1)

print("== step 2: layout + integrity ==")
store_root = None
for dirpath, dirnames, filenames in os.walk(os.path.join(TOOLPATH, ".store")):
    if "tools" in dirnames:
        store_root = dirpath; break
check("store layout found", store_root is not None, TOOLPATH)
tool_dir = os.path.join(store_root, "tools", "net10.0", "osx-arm64") if store_root else ""
for f in ["AiRaccoon.dll", "AiRaccoon.deps.json", "AiRaccoon.runtimeconfig.json",
          "libonnxruntime.dylib", "libsqlite3mc.dylib", "vec0.dylib"]:
    # libsqlite3mc.dylib is the only sqlite native since the 2.4.0 bundle switch
    # (e_sqlite3mc deprecated; libe_sqlite3.dylib no longer ships).
    check(f"present: {f}", os.path.isfile(os.path.join(tool_dir, f)))
model = os.path.join(tool_dir, "Models", "model_qint8_arm64.onnx")
vocab = os.path.join(tool_dir, "Models", "vocab.txt")
check("model sha256 pinned", sha(model) == MODEL_SHA256, sha(model))
check("vocab sha256 pinned", sha(vocab) == VOCAB_SHA256, sha(vocab))

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
        self.proc = subprocess.Popen([tool, "--data-root", dataroot, "--transport", "stdio"],
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
