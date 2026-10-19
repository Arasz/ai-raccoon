#!/usr/bin/env python3
"""Capture grep output asynchronously or retrieve a recent result as JSON."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import signal
import threading
import subprocess
import sys
import time
from typing import Any, Dict, Optional

import badger_store

TTL_SECONDS = 120
MAX_OUTPUT_BYTES = 1024 * 1024
KEY_PREFIX = "memory_first.raw_grep."


def digest(pattern: str) -> str:
    """Return the stable SHA-256 retrieval handle for an exact pattern."""
    return hashlib.sha256(pattern.encode("utf-8")).hexdigest()


def _scope_hash(cwd: Optional[str] = None) -> str:
    """Scope a retrieval to the current checkout/worktree."""
    return hashlib.sha256(os.path.realpath(cwd or os.getcwd()).encode("utf-8")).hexdigest()


def _storage_key(scope: str, pattern_hash: str) -> str:
    return f"{KEY_PREFIX}{scope}.{pattern_hash}"


def _store():
    """Open the ai-badger audit database used by the memory-first hooks."""
    return badger_store.open_audit(timeout=1.0)


def _read_bounded(stream: Any) -> tuple[bytes, bool]:
    """Drain a child stream while retaining at most MAX_OUTPUT_BYTES."""
    retained = bytearray()
    truncated = False
    while True:
        chunk = stream.read(65536)
        if not chunk:
            break
        room = MAX_OUTPUT_BYTES - len(retained)
        retained.extend(chunk[:room])
        truncated = truncated or len(chunk) > room
    return bytes(retained), truncated


def capture(capture_key: str, command: list[str]) -> None:
    """Run one executable directly and persist bounded output for no more than 120 seconds."""
    if not command or any(not isinstance(part, str) for part in command):
        return
    program = command[0].replace("\\", "/").rsplit("/", 1)[-1].lower()
    if program not in ("grep", "egrep", "fgrep", "rg", "rg.exe"):
        return
    scope, separator, encoded_hashes = capture_key.partition(":")
    if not separator or len(scope) != 64 or any(c not in "0123456789abcdef" for c in scope):
        return
    hashes = list(dict.fromkeys(encoded_hashes.split(",")))
    if not hashes or any(len(key) != 64 or any(c not in "0123456789abcdef" for c in key)
                         for key in hashes):
        return
    now = time.time()
    record: Dict[str, Any] = {
        "status": "pending", "created_at": now, "expires_at": now + TTL_SECONDS,
        "stdout": "", "stderr": "", "returncode": None,
    }
    store = _store()
    try:
        for key, value in store.kv_all("hook_state").items():
            if (key.startswith(KEY_PREFIX) and isinstance(value, dict)
                    and float(value.get("expires_at", 0)) <= now):
                store.kv_delete("hook_state", key)
        claimed_keys = []
        for key_hash in hashes:
            key = _storage_key(scope, key_hash)

            def claim(existing):
                if not isinstance(existing, dict) or float(existing.get("expires_at", 0)) <= now:
                    claimed_keys.append(key)
                    return record
                return existing

            store.kv_update("hook_state", key, claim, None)
        if not claimed_keys:
            return
    finally:
        store.close()
    try:
        options = {}
        if os.name == "posix":
            options["start_new_session"] = True
        elif hasattr(subprocess, "CREATE_NEW_PROCESS_GROUP"):
            options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
        process = subprocess.Popen(command, shell=False,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, **options)
        out_result: list[tuple[bytes, bool]] = []
        err_result: list[tuple[bytes, bool]] = []
        out_thread = threading.Thread(target=lambda: out_result.append(
            _read_bounded(process.stdout)), daemon=True)
        err_thread = threading.Thread(target=lambda: err_result.append(
            _read_bounded(process.stderr)), daemon=True)
        out_thread.start()
        err_thread.start()
        try:
            returncode = process.wait(timeout=60)
            status = "error" if returncode in (126, 127) else "ready"
        except subprocess.TimeoutExpired:
            if os.name == "posix":
                os.killpg(process.pid, signal.SIGKILL)
            else:
                process.kill()
            returncode = process.wait()
            status = "error"
        out_thread.join(timeout=5)
        err_thread.join(timeout=5)
        if out_thread.is_alive() or err_thread.is_alive():
            raise subprocess.SubprocessError("capture output pipes did not close")
        stdout, stdout_truncated = out_result[0]
        stderr, stderr_truncated = err_result[0]
        record.update(stdout=stdout.decode("utf-8", errors="replace"),
                      stderr=stderr.decode("utf-8", errors="replace"),
                      stdout_truncated=stdout_truncated,
                      stderr_truncated=stderr_truncated,
                      returncode=returncode, status=status)
    except (OSError, subprocess.SubprocessError) as error:
        record.update(status="error", error=str(error), stderr=str(error), returncode=1)
    store = _store()
    try:
        for key in claimed_keys:
            store.kv_set("hook_state", key, record)
    finally:
        store.close()


def get_result(pattern: Optional[str] = None,
               pattern_hash: Optional[str] = None) -> Dict[str, Any]:
    """Return a pending, ready, expired, or error response for one pattern handle."""
    if (pattern is None) == (pattern_hash is None):
        return {"status": "error", "error": "provide exactly one of pattern or pattern-hash"}
    key_hash = digest(pattern) if pattern is not None else str(pattern_hash)
    if len(key_hash) != 64 or any(char not in "0123456789abcdef" for char in key_hash):
        return {"status": "error", "error": "pattern-hash must be a SHA-256 hex digest"}
    try:
        store = _store()
        try:
            record = store.kv_get("hook_state", _storage_key(_scope_hash(), key_hash))
            if not isinstance(record, dict):
                return {"status": "pending", "hash": key_hash}
            if float(record.get("expires_at", 0)) <= time.time():
                store.kv_delete("hook_state", _storage_key(_scope_hash(), key_hash))
                return {"status": "expired", "hash": key_hash}
            return {"hash": key_hash, **record}
        finally:
            store.close()
    except Exception as error:  # pylint: disable=broad-exception-caught  # retrieval always emits JSON
        return {"status": "error", "error": str(error), "hash": key_hash,
                "returncode": 1}


def main(argv: Optional[list[str]] = None) -> int:
    """Run an internal capture worker or print one JSON retrieval response."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--capture", nargs=2, metavar=("PATTERN_HASH[,HASH...]", "COMMAND"))
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--pattern")
    group.add_argument("--pattern-hash")
    args = parser.parse_args(argv)
    if args.capture:
        try:
            command = json.loads(args.capture[1])
        except json.JSONDecodeError:
            return 0
        if isinstance(command, list):
            capture(args.capture[0], command)
        return 0
    print(json.dumps(get_result(args.pattern, args.pattern_hash), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
