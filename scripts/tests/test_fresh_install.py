"""fresh_install helpers pinned: sha() against hashlib, unwrap() against the MCP result shapes."""

import hashlib

import fresh_install


def test_sha_matches_hashlib_reference(tmp_path):
    # >1 MiB payload so the chunked read path is exercised, not just the single-shot case.
    p = tmp_path / "model.bin"
    p.write_bytes(b"payload\x00\x01" * (1 << 18))
    assert fresh_install.sha(str(p)) == hashlib.sha256(p.read_bytes()).hexdigest()


def test_sha_empty_file(tmp_path):
    p = tmp_path / "empty.bin"
    p.write_bytes(b"")
    assert fresh_install.sha(str(p)) == hashlib.sha256(b"").hexdigest()


def test_unwrap_parses_text_content_json():
    result = {"content": [{"type": "text", "text": '{"hash": "abc123", "context": "x"}'}]}
    assert fresh_install.unwrap(result) == {"hash": "abc123", "context": "x"}


def test_unwrap_error_result_still_parses_text_json():
    # Tool failures surface as isError: true with text content; the wrapper does not
    # special-case them, so the JSON payload unwraps the same way.
    result = {"isError": True, "content": [{"type": "text", "text": '{"error": "boom"}'}]}
    assert fresh_install.unwrap(result) == {"error": "boom"}


def test_unwrap_passthrough_without_text_content():
    assert fresh_install.unwrap({}) == {}
    assert fresh_install.unwrap(None) is None
    assert fresh_install.unwrap({"content": []}) == {"content": []}
    assert fresh_install.unwrap({"content": [{"type": "image", "data": "x"}]}) == {"content": [{"type": "image", "data": "x"}]}
