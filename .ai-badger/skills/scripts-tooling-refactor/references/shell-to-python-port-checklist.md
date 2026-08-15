# Shell→Python byte-identical port — worked example: verify-tool-package.sh → src/package_verify.py (P2)

## Message/stream/exit contract (preserve exactly)

| .sh | Python |
|---|---|
| FAIL lines → `>&2`, `exit 1` | `print(..., file=sys.stderr)`; `return 1` |
| present/verified/OK lines → stdout | `print(...)` |
| `dotnet pack` failure (set -e) → child's exit code | `subprocess.run(..., check=True)`; `except subprocess.CalledProcessError as exc: return exc.returncode` |
| RID undeterminable → stderr msg + `exit 1` | `print(..., file=sys.stderr)`; `raise SystemExit(1)` |

## Structure that keeps tests hermetic (no dotnet in unit tests)

- Adapter/pure split for the fallback chain:
  `_dotnet_rid()` — subprocess, returns None on OSError/SubprocessError/timeout/empty; `rid_from_uname(system, machine)` — pure dict table; `detect_rid()` — chain, SystemExit(1) at the end.
- Tests: monkeypatch `_dotnet_rid` (and the table) for the chain; `@pytest.mark.parametrize` the table; capsys asserts the stderr message for the SystemExit case.
- csproj parse: `re.search(r"<PackageVersion>([^<]*)</PackageVersion>", text)` — first match; `None` when tag absent. `[^<]*` (not `[^<]+`) matches sed's `[^<]*`, so an EMPTY `<PackageVersion></PackageVersion>` returns `""` → downstream "was not produced" path, exactly like the .sh.

## Entry-check loop ordering (the subtle one)

.sh iterates required entries in order, printing `present in package: X`
per present entry and FAILing on the FIRST missing one. Python must not fail
on `missing[0]` — compute the missing set, then loop entries in order
printing present-or-FAIL. Output byte-order is part of the contract.

## SHA-pin comparison tests (preimage problem)

You cannot construct zip content that hashes to a fixed pin — so the green
`actual == pin` branch is untestable with fake data. Cover instead:
1. pure `entry_sha256(zip, entry)` == `hashlib.sha256(known_content).hexdigest()` reference;
2. the MISMATCH branch, asserting the full `FAIL: model sha256 mismatch:
   expected <pin>, got <hash>` message contains the pin constant imported
   from the bundle module (this is the "vs pin" test);
3. the pin constant itself asserted in the bundle-contract test;
4. the real green path via end-to-end smoke.

## Full-green smoke recipe (beyond "runs sanely")

1. `git check-ignore -v <artifact>` — gitignored ⇒ safe to provision (Models/*.onnx was).
2. Provision via the repo's own P1 download script (double-smoke).
3. `which dotnet` may hit an ambient shim; find the real SDK and run
   `PATH=/real/bin:$PATH python3 scripts/verify-tool-package.py`.
4. Run once from a different cwd (`cd /tmp && python3 <abs path>`) to prove
   `__file__`-derived REPO_ROOT is invocation-independent.
5. Expected: byte-identical OK line + `exit=0`; the "was not produced" FAIL
   path also worth observing once (here via the shim).

## Behavior deltas to report (exit codes unchanged)

- Missing `<PackageVersion>`: .sh fails late with `FAIL: …/the project..nupkg was not produced` (empty version in filename); Python fails early with `FAIL: <PackageVersion> not found in <csproj>` — same exit 1, clearer.
- Temp dir: `${TMPDIR:-/tmp}` vs `tempfile.mkdtemp` (macOS defaults to /var/folders/…) — observable only in FAIL-path messages.
- RID line parse: awk any-line-contains vs Python startswith — identical on real `dotnet --info` output.
- unzip `-Z1`+grep-against-file SIGPIPE race: eliminated by zipfile (intended improvement, say so).
