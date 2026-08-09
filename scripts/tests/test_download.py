"""fetch_verified semantics, hermetic via file:// URLs and tmp files (no network)."""

import hashlib

import pytest

import download

CONTENT = b"pretend model bytes"


def _sha256(data):
    return hashlib.sha256(data).hexdigest()


def test_sha256_file_matches_hashlib(tmp_path):
    path = tmp_path / "f.bin"
    path.write_bytes(CONTENT)
    assert download.sha256_file(path) == _sha256(CONTENT)


def test_fetch_verified_skips_when_file_matches(tmp_path, capsys):
    target = tmp_path / "model.bin"
    target.write_bytes(CONTENT)
    result = download.fetch_verified("model.bin", "file:///nonexistent", _sha256(CONTENT), tmp_path)
    assert result == target
    assert target.read_bytes() == CONTENT
    assert "already downloaded and verified: %s" % target in capsys.readouterr().out


def test_fetch_verified_downloads_and_verifies(tmp_path, capsys):
    src = tmp_path / "src.bin"
    src.write_bytes(CONTENT)
    out_dir = tmp_path / "out"
    result = download.fetch_verified("model.bin", src.as_uri(), _sha256(CONTENT), out_dir)
    assert result == out_dir / "model.bin"
    assert (out_dir / "model.bin").read_bytes() == CONTENT
    out = capsys.readouterr().out
    assert "downloading model.bin -> %s" % (out_dir / "model.bin") in out
    assert "verified: %s" % (out_dir / "model.bin") in out


def test_fetch_verified_mismatch_deletes_file_and_raises(tmp_path, capsys):
    src = tmp_path / "src.bin"
    src.write_bytes(CONTENT)
    out_dir = tmp_path / "out"
    with pytest.raises(download.Sha256MismatchError):
        download.fetch_verified("model.bin", src.as_uri(), "0" * 64, out_dir)
    assert not (out_dir / "model.bin").exists()
    assert not (out_dir / "model.bin.part").exists()
    err = capsys.readouterr().err
    assert "SHA-256 mismatch for model.bin: expected %s, got %s" % ("0" * 64, _sha256(CONTENT)) in err


def test_fetch_verified_empty_download_fails_without_hashing_nothing(tmp_path, capsys):
    """An empty body is an empty download, not a SHA mismatch against the hash of nothing."""
    src = tmp_path / "empty.bin"
    src.write_bytes(b"")
    out_dir = tmp_path / "out"
    with pytest.raises(download.EmptyDownloadError) as excinfo:
        download.fetch_verified("model.bin", src.as_uri(), _sha256(CONTENT), out_dir)
    message = str(excinfo.value)
    assert src.as_uri() in message
    assert "0 bytes" in message
    assert not (out_dir / "model.bin").exists()
    assert not (out_dir / "model.bin.part").exists()
    assert _sha256(b"") not in capsys.readouterr().err


def test_fetch_verified_replaces_stale_file_on_mismatch(tmp_path):
    src = tmp_path / "src.bin"
    src.write_bytes(CONTENT)
    out_dir = tmp_path / "out"
    out_dir.mkdir()
    stale = out_dir / "model.bin"
    stale.write_bytes(b"stale bytes")
    with pytest.raises(download.Sha256MismatchError):
        download.fetch_verified("model.bin", src.as_uri(), "0" * 64, out_dir)
    assert not stale.exists()


def test_fetch_verified_failure_cleans_part_file(tmp_path):
    out_dir = tmp_path / "out"
    with pytest.raises(OSError):
        download.fetch_verified("model.bin", (tmp_path / "missing.bin").as_uri(), "0" * 64, out_dir)
    assert not (out_dir / "model.bin.part").exists()
    assert not (out_dir / "model.bin").exists()
