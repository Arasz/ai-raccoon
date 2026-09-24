"""Package verification: csproj version parse, RID detection, nupkg entry checks (port of verify-tool-package.sh)."""

import hashlib
import platform
import subprocess
import sys
import zipfile
from pathlib import Path

import bundle

_BUNDLED_PINS = tuple(("Models/%s/%s" % (bundle.BUNDLED_DIR, name), sha) for name, _url, sha in bundle.BUNDLED_FILES) + (
    ("Models/%s/%s" % (bundle.BUNDLED_DIR, bundle.BUNDLED_MANIFEST[0]), bundle.BUNDLED_MANIFEST[1]),
    ("Models/%s/%s" % (bundle.BUNDLED_DIR, bundle.BUNDLED_MLX_GRAPH[0]), bundle.BUNDLED_MLX_GRAPH[1]),
)
REQUIRED_ENTRIES = tuple(entry for entry, _sha in _BUNDLED_PINS) + ("Models/" + bundle.VOCAB_NAME,)

_RID_BY_UNAME = {
    ("Darwin", "arm64"): "osx-arm64",
    ("Darwin", "x86_64"): "osx-x64",
    ("Linux", "x86_64"): "linux-x64",
    ("Linux", "aarch64"): "linux-arm64",
}


def read_version(csproj_path):
    """Return the repo-root VERSION for a csproj, or None when no VERSION is above it.

    The version moved out of the csproj into a single repo-root VERSION file; walking up
    from the project keeps this working wherever the checkout is rooted.
    """
    for directory in Path(csproj_path).resolve().parents:
        version_file = directory / "VERSION"
        if version_file.is_file():
            return version_file.read_text().strip() or None
    return None


def rid_from_uname(system, machine):
    """Map a uname system/machine pair to a .NET RID (the .sh fallback table)."""
    return _RID_BY_UNAME.get((system, machine))


def _dotnet_rid():
    """Return the RID reported by `dotnet --info`, or None when unavailable."""
    try:
        out = subprocess.run(["dotnet", "--info"], capture_output=True, text=True, timeout=30).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    for line in out.splitlines():
        if line.strip().startswith("RID:"):
            return line.split(":", 1)[1].strip() or None
    return None


def detect_rid():
    """Return the host RID: dotnet --info first, uname mapping fallback, else exit 1."""
    rid = _dotnet_rid()
    if rid:
        return rid
    rid = rid_from_uname(platform.system(), platform.machine())
    if rid:
        return rid
    print("cannot determine host RID (dotnet --info unavailable)", file=sys.stderr)
    raise SystemExit(1)


def entries_matching(nupkg, suffix):
    """Stored paths in the nupkg ending in suffix — a bundled file may live at the package root or
    under tools/net10.0/<rid>/…, and this matches it wherever it was packed."""
    with zipfile.ZipFile(nupkg) as zf:
        names = zf.namelist()
    return [name for name in names if name.endswith(suffix)]


def missing_entries(nupkg):
    """Required entries with no path in the nupkg ending in them."""
    return [entry for entry in REQUIRED_ENTRIES if not entries_matching(nupkg, entry)]


def duplicate_entries(nupkg):
    """Required entries packed at more than one path, as (entry, matching_paths) pairs."""
    duplicates = []
    for entry in REQUIRED_ENTRIES:
        matches = entries_matching(nupkg, entry)
        if len(matches) > 1:
            duplicates.append((entry, matches))
    return duplicates


def entry_sha256(nupkg, path):
    """Lowercase hex SHA-256 of the stored bytes of the exact path in the nupkg."""
    with zipfile.ZipFile(nupkg) as zf:
        return hashlib.sha256(zf.read(path)).hexdigest()


def verify_nupkg(nupkg, version):
    """Check every required entry is packed exactly once with the right sha; print and return 0/1."""
    missing = missing_entries(nupkg)
    for entry in missing:
        print("FAIL: %s missing from %s" % (entry, nupkg), file=sys.stderr)
    if missing:
        return 1
    duplicates = duplicate_entries(nupkg)
    for entry, matches in duplicates:
        print("FAIL: %s packed %d times in %s: %s" % (entry, len(matches), nupkg, ", ".join(matches)), file=sys.stderr)
    if duplicates:
        return 1
    for entry in REQUIRED_ENTRIES:
        print("present in package: %s" % entry)
    for entry, expected in _BUNDLED_PINS:
        [path] = entries_matching(nupkg, entry)
        actual = entry_sha256(nupkg, path)
        if actual != expected:
            print("FAIL: %s sha256 mismatch: expected %s, got %s" % (entry, expected, actual), file=sys.stderr)
            return 1
        print("sha256 verified: %s" % entry)
    print("OK: ai-raccoon.%s.nupkg ships the verified bundled model exactly once" % version)
    return 0


def verify_no_bundled_weights(nupkg):
    """The RID-agnostic shim package ships no copy of the bundled weights of its own; print and
    return 0/1 — the RID-specific packages are the only ones that need to carry them."""
    present = [entry for entry in REQUIRED_ENTRIES if entries_matching(nupkg, entry)]
    for entry in present:
        print("FAIL: %s unexpectedly present in top-level %s" % (entry, nupkg), file=sys.stderr)
    if present:
        return 1
    print("OK: %s ships no bundled weights" % nupkg)
    return 0


def pack_and_verify(csproj, rid, out_dir):
    """dotnet pack csproj into out_dir for rid, then verify the RID-specific nupkg carries the
    bundled model exactly once and the top-level shim package carries none of it."""
    version = read_version(csproj)
    if version is None:
        print("FAIL: no VERSION file above %s" % csproj, file=sys.stderr)
        return 1
    try:
        subprocess.run(
            [
                "dotnet",
                "pack",
                str(csproj),
                "-c",
                "Release",
                "-p:RuntimeIdentifiers=%s" % rid,
                "-o",
                str(out_dir),
                "--nologo",
            ],
            check=True,
        )
    except subprocess.CalledProcessError as exc:
        return exc.returncode
    rid_nupkg = Path(out_dir) / ("ai-raccoon.%s.%s.nupkg" % (rid, version))
    if not rid_nupkg.is_file():
        print("FAIL: %s was not produced" % rid_nupkg, file=sys.stderr)
        return 1
    result = verify_nupkg(rid_nupkg, version)
    if result != 0:
        return result
    top_nupkg = Path(out_dir) / ("ai-raccoon.%s.nupkg" % version)
    if top_nupkg.is_file():
        result = verify_no_bundled_weights(top_nupkg)
    return result
