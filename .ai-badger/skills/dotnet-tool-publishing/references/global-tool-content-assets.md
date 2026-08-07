# Global-tool content assets: the 1.0.4 case (measured 2026-08-05)

Full worked case behind the "Bundled content assets" section of SKILL.md.

## Incident

The installed tool (<pkg-id> 1.0.4, macOS arm64) ran but `memory_search` failed:
the embedding model could not be resolved. Two independent layers:

1. **Settings value resolved as a cwd-relative path.** The bank's settings table held
   `embedding.model = nomic-embed-text` — a model NAME left by an earlier configuration.
   The resolver (`EmbeddingService.CreateLocal`) treats any non-empty model value as a
   filesystem path (`Path.GetFullPath(settings.Model)`), so the name resolved against the
   RUNNING SERVER'S CWD (an unrelated integration worktree) and the ONNX load died with a
   cryptic file-not-found. Fix shape: fail fast when the configured path does not exist,
   message names the resolved path and both remediations — `'<tool> model set local'`
   (bundled) or `'<tool> model set local <path-to-onnx>'` (custom). The settings row
   itself is cleared by re-running `model set local` with no argument (null → row deleted,
   engine fingerprint stays `local:bundled`).
2. **The packaged tool shipped WITHOUT its main asset.** The csproj packs
   `Models/*.onnx` (gitignored — present only after `scripts/download-embedding-model.sh`)
   and `Models/vocab.txt` (committed). The publish workflow packs on a FRESH runner, where
   the gitignored glob matches nothing — so the nupkg shipped vocab.txt but no ONNX. The
   store had vocab.txt in three locations and the ONNX in none.

## Installed store layout (measured)

```
~/.dotnet/tools/.store/<pkg-id>/1.0.4/
├── <pkg-id>/1.0.4/                      # shell package (root also carries Models/)
│   └── Models/vocab.txt
└── <pkg-id>.osx-arm64/1.0.4/
    ├── Models/vocab.txt                          # payload root copy
    └── tools/net10.0/osx-arm64/
        ├── <tool>.dll ...                          # AppContext.BaseDirectory
        └── Models/vocab.txt                      # FIRST walk-up hit
```

`BundledModel.ResolveBundled` walks UP from `AppContext.BaseDirectory` checking
`Models/<file>` at each ancestor, so the payload tools dir's `Models/` wins. Manual fix =
copy the SHA-verified ONNX (sha256 == the pinned `ModelSha256`) into exactly that dir.

## Diagnosis moves that worked

- **The tool shim is a native Mach-O apphost**, not a shell script — `cat` dumps binary.
  No dll path readable from it.
- **`lsof -p <pid> -iTCP -sTCP:LISTEN`** on the running tool found the port (5094) AND the
  exact store paths (`txt REG` lines point at `.../osx-arm64/<tool>.dll` etc.) AND the
  process cwd (which turned out to be the cause of the cwd-relative resolution bug).
- **No restart needed after provisioning**: settings and files are read per call (the
  generator cache is keyed by fingerprint; a failed `GetOrAdd` factory caches nothing), so
  the running server picked up the copied file on the next search.

## Verification

- `shasum -a 256` of the copied file == the constant pinned in `BundledModel.cs`
  (`ModelSha256`), which the download script also pins — copy from the repo's
  `src/<Tool>/Models/` is valid by construction.
- Live proof over MCP Streamable HTTP with curl:
  `POST /mcp` with `Accept: application/json, text/event-stream`; initialize first, then
  `tools/call memory_search` — SSE body, `data:` line holds the JSON-RPC result. A
  healthy hybrid search returns fused ranking scores (~1.0 / 0.98 / 0.96) — a single
  modality would show 1.0 / 0.5 / 0.33, so the vector path demonstrably ran.

## Durable fix shipped afterwards (the hardening task)

- publish.yml gains a `bash scripts/download-embedding-model.sh` step between
  setup-dotnet and Pack (SHA-pinned, idempotent, fails loudly on mismatch; each matrix
  job downloads its own ~23 MB copy).
- `scripts/verify-tool-package.sh`: pack to a temp dir → `unzip -l` asserts both
  `Models/` entries in the shell nupkg → extract the ONNX and compare sha256 to the pin.
- Server startup calls `BundledModel.EnsureAsync` best-effort (locate-verified first,
  download only when absent, never throws — errors become a stderr warning, boot
  continues), with a ~30 s linked-cts timeout on the download.
- Resolver fail-fast + message contract; stale env-var guidance
  (`<APP>_EMBEDDING_MODEL`, `--embedding-model` — both deleted by the single-channel
  CLI refactor) removed from code, script, and packaged README. Historical docs
  (`docs/plans/*`, `docs/work/*`) recording the deletion stay untouched.
