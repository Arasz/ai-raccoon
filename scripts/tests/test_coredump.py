"""coredump.summarize over a synthetic ELF core built in-test (no real dump, no debugger)."""

import struct

import pytest

import coredump

LIBCORECLR = 0x400000
MEMORY_SO = 0x500000
ALT_STACK = 0x70000000
ALT_STACK_SIZE = 0x1000
CRASHED_TID = 101
FAULT_RIP = MEMORY_SO + 0x44
USER_CSGSFS = 0x002B000000000033


def _note(ntype, desc):
    name = b"CORE\0"
    padded_name = name + b"\0" * (-len(name) % 4)
    padded_desc = desc + b"\0" * (-len(desc) % 4)
    return struct.pack("<III", len(name), len(desc), ntype) + padded_name + padded_desc


def _prstatus(tid, cursig, rip, rsp=0):
    desc = bytearray(336)
    struct.pack_into("<h", desc, coredump.PRSTATUS_CURSIG, cursig)
    struct.pack_into("<i", desc, coredump.PRSTATUS_PID, tid)
    struct.pack_into("<Q", desc, coredump.PRSTATUS_REGS + 16 * 8, rip)
    struct.pack_into("<Q", desc, coredump.PRSTATUS_REGS + 19 * 8, rsp)
    return _note(coredump.NT_PRSTATUS, bytes(desc))


def _nt_file(entries):
    desc = struct.pack("<QQ", len(entries), 4096)
    for start, end, name in entries:
        desc += struct.pack("<QQQ", start, end, 0)
    for _, _, name in entries:
        desc += name.encode() + b"\0"
    return _note(coredump.NT_FILE, desc)


def _siginfo(signo, code, addr):
    return _note(coredump.NT_SIGINFO, struct.pack("<iii", signo, 0, code) + b"\0" * 4 + struct.pack("<Q", addr))


def _alt_stack_with_signal_frame(frame_offset, trapno=13, csgsfs=USER_CSGSFS, rip=FAULT_RIP):
    stack = bytearray(ALT_STACK_SIZE)
    gregs = frame_offset + coredump.SIGFRAME_GREGS
    struct.pack_into("<Q", stack, gregs + coredump.GREG_RIP * 8, rip)
    struct.pack_into("<Q", stack, gregs + coredump.GREG_CSGSFS * 8, csgsfs)
    struct.pack_into("<Q", stack, gregs + coredump.GREG_TRAPNO * 8, trapno)
    struct.pack_into("<Q", stack, gregs + coredump.GREG_CR2 * 8, 0)
    return bytes(stack)


def _write_core(path, notes, stack):
    note_blob = b"".join(notes)
    header_size = 64 + 2 * 56
    note_offset = header_size
    load_offset = note_offset + len(note_blob)

    header = bytearray(64)
    header[0:4] = b"\x7fELF"
    header[4:7] = bytes([2, 1, 1])
    struct.pack_into("<HH", header, 16, coredump.ET_CORE, coredump.EM_X86_64)
    struct.pack_into("<Q", header, 32, 64)
    struct.pack_into("<HH", header, 54, 56, 2)

    note_phdr = struct.pack("<IIQQQQQQ", coredump.PT_NOTE, 4, note_offset, 0, 0, len(note_blob), len(note_blob), 4)
    load_phdr = struct.pack("<IIQQQQQQ", coredump.PT_LOAD, 6, load_offset, ALT_STACK, ALT_STACK,
                            len(stack), len(stack), 4096)

    path.write_bytes(bytes(header) + note_phdr + load_phdr + note_blob + stack)
    return str(path)


def _core(tmp_path, **frame):
    frame_offset = 0x200
    notes = [
        _prstatus(100, 0, LIBCORECLR + 0x10),
        _prstatus(CRASHED_TID, 11, LIBCORECLR + 0x20, rsp=ALT_STACK + 0x100),
        _prstatus(102, 0, MEMORY_SO + 0x30),
        _prstatus(103, 0, MEMORY_SO + 0x30),
        _siginfo(11, 128, 0),
        _nt_file([
            (LIBCORECLR, LIBCORECLR + 0x100000, "/usr/share/dotnet/libcoreclr.so"),
            (MEMORY_SO, MEMORY_SO + 0x100000, "/home/runner/assets/memory.so"),
        ]),
    ]
    return _write_core(tmp_path / "core.dmp", notes, _alt_stack_with_signal_frame(frame_offset, **frame))


def test_summarize_names_the_signal_and_thread_count(tmp_path):
    lines = coredump.summarize(_core(tmp_path))
    assert any("4 threads" in line for line in lines)
    assert any("SIGSEGV (11)" in line for line in lines)


def test_summarize_reports_the_interrupted_fault_not_the_handler(tmp_path):
    lines = coredump.summarize(_core(tmp_path))
    assert any("tid=%d" % CRASHED_TID in line for line in lines)
    # The handler runs in libcoreclr; the fault the signal frame preserved is in memory.so.
    assert any("faulted at: memory.so+0x44" in line for line in lines)
    assert any("#GP" in line for line in lines)


def test_summarize_counts_threads_per_module(tmp_path):
    lines = coredump.summarize(_core(tmp_path))
    assert any(line.strip() == "2  /home/runner/assets/memory.so" for line in lines)
    assert any(line.strip() == "2  /usr/share/dotnet/libcoreclr.so" for line in lines)


def test_summarize_says_so_when_the_interrupted_context_is_not_recoverable(tmp_path):
    # A frame whose saved CS is not user-mode is not a signal frame; nothing may be inferred from it.
    lines = coredump.summarize(_core(tmp_path, csgsfs=0))
    assert any("not recovered" in line for line in lines)
    assert not any("faulted at" in line for line in lines)


def test_summarize_rejects_a_file_that_is_not_a_core_dump(tmp_path):
    path = tmp_path / "not-a-core"
    path.write_bytes(b"\x7fELF" + b"\0" * 60)
    with pytest.raises(ValueError):
        coredump.summarize(str(path))
