"""`checklist-poison-embedding-stub.py`: the OpenAI-compatible engine the checklist's poison-row item runs.

The item proves a memory row that can never embed is isolated and abandoned (AiRaccoon
ADR-0119). That needs an engine which fails for one input and answers for every other, and
whose log shows both kinds of request, so the stub is started for real on port 0 here and
driven over HTTP the way the .NET OpenAI SDK drives it.
"""
from __future__ import annotations

import base64
import json
import struct
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
STUB = ROOT / "scripts/checklist-poison-embedding-stub.py"
MARKER = "POISON-7Q"


@pytest.fixture(name="stub")
def _stub(tmp_path):
    log = tmp_path / "stub.log"
    proc = subprocess.Popen(
        [sys.executable, str(STUB), "--port", "0", "--marker", MARKER, "--dims", "8",
         "--log", str(log)],
        stdout=subprocess.PIPE, text=True)
    try:
        base = proc.stdout.readline().split()[-1]
        yield base, log
    finally:
        proc.terminate()
        proc.wait(timeout=5)


def _post(url, body):
    request = urllib.request.Request(url, data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=5) as response:
        return json.loads(response.read())


def test_it_prints_a_base_url_ending_in_v1(stub):
    base, _ = stub

    assert base.startswith("http://127.0.0.1:")
    assert base.endswith("/v1")


def test_a_float_request_gets_one_vector_per_input(stub):
    base, _ = stub

    body = _post(f"{base}/embeddings", {"model": "m", "input": ["alpha", "beta"]})

    assert [d["index"] for d in body["data"]] == [0, 1]
    assert all(len(d["embedding"]) == 8 for d in body["data"])
    assert body["data"][0]["embedding"] != body["data"][1]["embedding"]


def test_a_base64_request_gets_little_endian_float32(stub):
    """The .NET OpenAI SDK asks for base64; a float-only stub fails at the SDK, not the server."""
    base, _ = stub

    floats = _post(f"{base}/embeddings", {"model": "m", "input": ["alpha"]})
    packed = _post(f"{base}/embeddings",
                   {"model": "m", "input": ["alpha"], "encoding_format": "base64"})

    raw = base64.b64decode(packed["data"][0]["embedding"])
    decoded = list(struct.unpack("<8f", raw))
    assert decoded == pytest.approx(floats["data"][0]["embedding"], abs=1e-6)


def test_the_same_text_always_gets_the_same_vector(stub):
    base, _ = stub

    first = _post(f"{base}/embeddings", {"model": "m", "input": "alpha"})
    second = _post(f"{base}/embeddings", {"model": "m", "input": "alpha"})

    assert first["data"][0]["embedding"] == second["data"][0]["embedding"]


def test_a_batch_holding_the_marker_is_refused_with_400(stub):
    base, _ = stub

    with pytest.raises(urllib.error.HTTPError) as refused:
        _post(f"{base}/embeddings", {"model": "m", "input": ["fine", f"bad {MARKER} row"]})

    assert refused.value.code == 400


def test_any_other_path_is_404(stub):
    base, _ = stub

    with pytest.raises(urllib.error.HTTPError) as missing:
        _post(f"{base}/chat/completions", {"model": "m"})

    assert missing.value.code == 404


def test_the_log_counts_ok_and_poison_requests(stub):
    """The item's negative control: both kinds of request must reach the engine."""
    base, log = stub
    _post(f"{base}/embeddings", {"model": "m", "input": ["a", "b", "c"]})
    with pytest.raises(urllib.error.HTTPError):
        _post(f"{base}/embeddings", {"model": "m", "input": [MARKER]})

    assert log.read_text(encoding="utf-8").splitlines() == ["ok 3", "poison 1"]
