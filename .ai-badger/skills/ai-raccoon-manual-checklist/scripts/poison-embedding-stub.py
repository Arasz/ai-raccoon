#!/usr/bin/env python3
# pylint: disable=invalid-name  # hyphenated script filename is the shipped CLI name
"""An OpenAI-compatible embedding endpoint that refuses any batch holding a marker.

POST <base>/embeddings answers with deterministic vectors, as float lists or as base64
little-endian float32 (`encoding_format: "base64"`, which the .NET OpenAI SDK requests), and
answers 400 when any input contains the marker. Each request appends `ok N` or `poison N` to
the log, N being the number of inputs. Prints the base URL on its first stdout line.

Usage: poison-embedding-stub.py [--port 0] [--marker POISON-7Q] [--dims 384] [--log stub.log]
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import math
import struct
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


def vector(text: str, dims: int) -> list[float]:
    """A unit vector derived from the SHA-256 of the text, the same on every call."""
    values = []
    counter = 0
    while len(values) < dims:
        digest = hashlib.sha256(f"{counter}:{text}".encode()).digest()
        values.extend(b / 127.5 - 1.0 for b in digest)
        counter += 1
    values = values[:dims]
    norm = math.sqrt(sum(v * v for v in values)) or 1.0
    return [struct.unpack("<f", struct.pack("<f", v / norm))[0] for v in values]


def encode(values: list[float], encoding_format: str | None) -> list[float] | str:
    """The embedding field in the shape the caller asked for."""
    if encoding_format == "base64":
        return base64.b64encode(struct.pack(f"<{len(values)}f", *values)).decode()
    return values


def make_handler(marker: str, dims: int, log: Path):
    """A request handler bound to one marker, vector size and log file."""

    class Handler(BaseHTTPRequestHandler):
        """Serves POST .../embeddings; every other request is 404."""

        def do_POST(self):  # pylint: disable=invalid-name  # http.server dispatch name
            """Embed the inputs, or refuse the batch when one holds the marker."""
            length = int(self.headers.get("Content-Length", 0))
            body = json.loads(self.rfile.read(length) or b"{}")
            if not self.path.rstrip("/").endswith("/embeddings"):
                self._reply(404, {"error": {"message": f"no route {self.path}"}})
                return
            inputs = body.get("input", [])
            inputs = [inputs] if isinstance(inputs, str) else list(inputs)
            if any(marker in str(text) for text in inputs):
                self._record(f"poison {len(inputs)}")
                self._reply(400, {"error": {"message": f"input contains {marker}",
                                            "type": "invalid_request_error"}})
                return
            self._record(f"ok {len(inputs)}")
            data = [{"object": "embedding", "index": i,
                     "embedding": encode(vector(str(text), dims), body.get("encoding_format"))}
                    for i, text in enumerate(inputs)]
            self._reply(200, {"object": "list", "data": data, "model": body.get("model", ""),
                              "usage": {"prompt_tokens": 0, "total_tokens": 0}})

        def _record(self, line: str) -> None:
            with log.open("a", encoding="utf-8") as handle:
                handle.write(line + "\n")

        def _reply(self, status: int, payload: dict) -> None:
            raw = json.dumps(payload).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(raw)))
            self.end_headers()
            self.wfile.write(raw)

        def log_message(self, format, *args):  # pylint: disable=redefined-builtin
            """Keep stdout to the one base-URL line."""

    return Handler


def main(argv: list[str]) -> int:
    """Parse the options, print the base URL and serve until killed."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--marker", default="POISON-7Q")
    parser.add_argument("--dims", type=int, default=384)
    parser.add_argument("--log", type=Path, default=Path("stub.log"))
    args = parser.parse_args(argv)
    server = ThreadingHTTPServer(("127.0.0.1", args.port),
                                 make_handler(args.marker, args.dims, args.log))
    print(f"listening on http://127.0.0.1:{server.server_address[1]}/v1", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
