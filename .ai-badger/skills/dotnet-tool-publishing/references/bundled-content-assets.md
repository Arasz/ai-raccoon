## Bundled content assets: gitignored pack globs, store layout, provisioning (verified 2026-08-05)

A `PackAsTool` project that ships runtime assets (embedding models, fixtures, native libs)
has a silent failure mode distinct from the MSB3030 trap:

- **A pack-time glob over a GITIGNORED dir matches nothing on a fresh runner.** `<None
  Include="Models/*.onnx" Pack="true" PackagePath="Models/">` packs only files present in
  the checkout; CI checkout has no untracked files, so the glob is empty and the nupkg ships
  WITHOUT the asset — while a COMMITTED sibling (`vocab.txt`) ships fine. The tool installs,
  then fails at runtime with "<asset> not found next to the tool". Verify pack CONTENT, not
  pack success: `unzip -l <shell>.nupkg` (and the RID payload) and diff against the csproj's
  intent.
- **Fix: provision gitignored assets in CI BEFORE pack** — a SHA-pinned, idempotent download
  step (skip-if-present-and-verified, fail loudly on SHA mismatch) between setup-dotnet and
  Pack; note the dependency in the workflow header comment so nobody reorders it.
- **Installed store layout (global tool):** AppContext.BaseDirectory of the RID payload is
  `~/.dotnet/tools/.store/<PackageId>/<ver>/<PackageId>.<rid>/<ver>/tools/net10.0/<rid>/`.
  Content assets land in up to THREE store locations (payload tools dir, payload root, shell
  root). Resolvers that walk UP from BaseDirectory checking `Models/` at each ancestor hit the
  payload tools dir FIRST — that is where manual provisioning must copy.
- **The tool shim is a native Mach-O apphost, not a script** — no dll path inside. For a
  RUNNING tool, `lsof -p <pid> -iTCP -sTCP:LISTEN` reveals the port and the exact store paths
  (`txt REG` lines) plus the process cwd — the cwd also explains cwd-relative asset-resolution
  bugs (a settings value resolved via `Path.GetFullPath` picks up the server's working dir).
- **Manual provisioning without reinstall:** copy the SHA-verified asset next to its committed
  sibling in the first-hit dir. No restart needed when resolution is per-call (filesystem /
  settings read at call time, generator cached by fingerprint) — verified live: a search
  succeeded on the running server immediately after the copy.
- **Runtime self-heal:** production code that can fetch the missing asset at startup
  (`BundledModel.EnsureAsync` pattern: locate-and-verify first, download only when absent,
  NEVER throw — collect errors, warn to stderr, keep booting) turns a broken package into a
  recoverable install; bound the download with a ~30 s linked-cts timeout so slow networks
  cannot stall boot. Fail-fast in the RESOLVER (clear message naming the resolved path and
  both remediations when a configured asset path does not exist) converts cryptic runtime
  errors into actionable ones.

Full measured case (1.0.4): `references/global-tool-content-assets.md`.
