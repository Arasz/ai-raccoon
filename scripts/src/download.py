"""Verified download helpers: sha256_file() and fetch_verified() (urllib, no curl)."""

import hashlib
import sys
import urllib.request
from pathlib import Path


class Sha256MismatchError(Exception):
    """Downloaded file did not match the expected SHA-256."""


class EmptyDownloadError(Exception):
    """Download completed but produced no bytes to verify."""


def sha256_file(path):
    """Return the lowercase hex SHA-256 of the file at path."""
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _progress(blocks, block_size, total):
    # curl --progress-bar equivalent: rewrite the percentage on one line.
    if total > 0:
        percent = min(100, blocks * block_size * 100 // total)
        sys.stdout.write("\r%d%%" % percent)
        sys.stdout.flush()


def fetch_verified(name, url, expected_sha, target_dir):
    """Download name into target_dir unless already present and verified; delete on SHA mismatch."""
    target_dir = Path(target_dir)
    target_dir.mkdir(parents=True, exist_ok=True)
    target = target_dir / name
    if target.is_file() and sha256_file(target) == expected_sha:
        print("already downloaded and verified: %s" % target)
        return target
    print("downloading %s -> %s" % (name, target))
    part = target_dir / (name + ".part")
    try:
        urllib.request.urlretrieve(url, str(part), _progress)
    except Exception:
        part.unlink(missing_ok=True)
        raise
    finally:
        sys.stdout.write("\n")
        sys.stdout.flush()
    if part.stat().st_size == 0:
        part.unlink(missing_ok=True)
        raise EmptyDownloadError("%s returned 0 bytes; nothing was downloaded to verify" % url)
    part.replace(target)
    actual = sha256_file(target)
    if actual != expected_sha:
        print("SHA-256 mismatch for %s: expected %s, got %s" % (name, expected_sha, actual), file=sys.stderr)
        target.unlink(missing_ok=True)
        raise Sha256MismatchError(name)
    print("verified: %s" % target)
    return target
