"""Package verification: csproj version parse, RID detection, nupkg entry checks (port of verify-tool-package.sh)."""

import hashlib
import platform
import re
import subprocess
import sys
import zipfile
from pathlib import Path

import bundle

REQUIRED_ENTRIES = ("Models/" + bundle.MODEL_NAME, "Models/" + bundle.VOCAB_NAME)

_RID_BY_UNAME = {
    ("Darwin", "arm64"): "osx-arm64",
    ("Darwin", "x86_64"): "osx-x64",
    ("Linux", "x86_64"): "linux-x64",
    ("Linux", "aarch64"): "linux-arm64",
}


def parse_csproj_version(path):
    """Return the first <PackageVersion> value in the csproj, or None when absent."""
    text = Path(path).read_text()
    match = re.search(r"<PackageVersion>([^<]*)</PackageVersion>", text)
    return match.group(1) if match else None


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


def missing_entries(nupkg):
    """Required entries absent from the nupkg (exact stored-name match)."""
    with zipfile.ZipFile(nupkg) as zf:
        names = set(zf.namelist())
    return [entry for entry in REQUIRED_ENTRIES if entry not in names]


def entry_sha256(nupkg, entry):
    """Lowercase hex SHA-256 of the stored bytes of entry in the nupkg."""
    with zipfile.ZipFile(nupkg) as zf:
        return hashlib.sha256(zf.read(entry)).hexdigest()


def verify_nupkg(nupkg, version):
    """Check required entries and the model sha against the bundle pins; print and return 0/1."""
    missing = missing_entries(nupkg)
    for entry in REQUIRED_ENTRIES:
        if entry in missing:
            print("FAIL: %s missing from %s" % (entry, nupkg), file=sys.stderr)
            return 1
        print("present in package: %s" % entry)
    actual = entry_sha256(nupkg, "Models/" + bundle.MODEL_NAME)
    if actual != bundle.MODEL_SHA256:
        print("FAIL: model sha256 mismatch: expected %s, got %s" % (bundle.MODEL_SHA256, actual), file=sys.stderr)
        return 1
    print("model sha256 verified: %s" % actual)
    print("OK: ai-raccoon.%s.nupkg ships the verified bundled model" % version)
    return 0


def pack_and_verify(csproj, rid, out_dir):
    """dotnet pack csproj into out_dir, then verify the produced nupkg; return the exit code."""
    version = parse_csproj_version(csproj)
    if version is None:
        print("FAIL: <PackageVersion> not found in %s" % csproj, file=sys.stderr)
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
    nupkg = Path(out_dir) / ("ai-raccoon.%s.nupkg" % version)
    if not nupkg.is_file():
        print("FAIL: %s was not produced" % nupkg, file=sys.stderr)
        return 1
    return verify_nupkg(nupkg, version)
