"""Triage for Linux ELF core dumps: which signal, which module faulted, which threads ran where.

Reads only the ELF notes and the crashing thread's signal frame, so a multi-GB dump
summarises in milliseconds without a debugger or matching symbols.
"""

import bisect
import struct
from collections import Counter

ET_CORE = 4
EM_X86_64 = 62
PT_LOAD = 1
PT_NOTE = 4
NT_PRSTATUS = 1
NT_FILE = 0x46494C45
NT_SIGINFO = 0x53494749

# elf_prstatus (x86_64): pr_cursig at 12, pr_pid at 32, pr_reg at 112 (27 * 8 bytes).
PRSTATUS_CURSIG = 12
PRSTATUS_PID = 32
PRSTATUS_REGS = 112
REG_RIP = 16

# rt_sigframe: pretcode, then ucontext; uc_mcontext.gregs starts 48 bytes into the frame.
SIGFRAME_GREGS = 48
GREG_RIP = 16
GREG_CSGSFS = 18
GREG_TRAPNO = 20
GREG_CR2 = 22
USER_CS = 0x33

SIGNALS = {4: "SIGILL", 6: "SIGABRT", 7: "SIGBUS", 8: "SIGFPE", 11: "SIGSEGV"}

TRAPS = {
    6: "#UD invalid opcode",
    13: "#GP general protection — a non-canonical or otherwise illegal address",
    14: "#PF page fault",
}


class CoreDump:
    """The notes and loadable segments of an ELF core dump."""

    def __init__(self, path):
        self.path = path
        self._file = open(path, "rb")
        header = self._file.read(64)
        if header[:4] != b"\x7fELF":
            raise ValueError("%s is not an ELF file" % path)
        e_type, e_machine = struct.unpack_from("<HH", header, 16)
        if e_type != ET_CORE:
            raise ValueError("%s is not a core dump (e_type %d)" % (path, e_type))
        if e_machine != EM_X86_64:
            raise ValueError("%s is not x86-64 (e_machine %d)" % (path, e_machine))

        phoff = struct.unpack_from("<Q", header, 32)[0]
        phentsize, phnum = struct.unpack_from("<HH", header, 54)
        self._file.seek(phoff)
        raw = self._file.read(phentsize * phnum)

        self._loads = []
        notes = []
        for i in range(phnum):
            p_type = struct.unpack_from("<I", raw, i * phentsize)[0]
            p_offset, p_vaddr, _, p_filesz = struct.unpack_from("<QQQQ", raw, i * phentsize + 8)
            if p_type == PT_LOAD and p_filesz > 0:
                self._loads.append((p_vaddr, p_vaddr + p_filesz, p_offset))
            elif p_type == PT_NOTE:
                notes.append((p_offset, p_filesz))
        self._loads.sort()
        self._load_starts = [load[0] for load in self._loads]

        self.notes = [note for segment in notes for note in self._read_notes(*segment)]
        self.mappings = self._read_mappings()
        self._map_starts = [m[0] for m in self.mappings]
        self._bases = {}
        for start, _, file_offset, name in self.mappings:
            if file_offset == 0:
                self._bases.setdefault(name, start)

    def close(self):
        self._file.close()

    def _read_notes(self, offset, size):
        self._file.seek(offset)
        data = self._file.read(size)
        pos = 0
        while pos + 12 <= len(data):
            namesz, descsz, ntype = struct.unpack_from("<III", data, pos)
            pos += 12 + ((namesz + 3) & ~3)
            yield ntype, data[pos:pos + descsz]
            pos += (descsz + 3) & ~3

    def _read_mappings(self):
        mappings = []
        for ntype, desc in self.notes:
            if ntype != NT_FILE:
                continue
            count = struct.unpack_from("<Q", desc, 0)[0]
            pos = 16
            entries = []
            for _ in range(count):
                start, end, file_offset = struct.unpack_from("<QQQ", desc, pos)
                pos += 24
                entries.append((start, end, file_offset))
            names = desc[pos:].split(b"\0")
            for i, (start, end, file_offset) in enumerate(entries):
                name = names[i].decode("utf-8", "replace") if i < len(names) else "?"
                mappings.append((start, end, file_offset, name))
        mappings.sort()
        return mappings

    def read(self, address, length):
        """Return length bytes of process memory at address, or None when not in the dump."""
        i = bisect.bisect_right(self._load_starts, address) - 1
        if i < 0:
            return None
        start, end, offset = self._loads[i]
        if not start <= address < end:
            return None
        self._file.seek(offset + (address - start))
        return self._file.read(min(length, end - address))

    def module_of(self, address):
        """Return the mapped file holding address, or None."""
        i = bisect.bisect_right(self._map_starts, address) - 1
        if i >= 0 and self.mappings[i][0] <= address < self.mappings[i][1]:
            return self.mappings[i][3]
        return None

    def describe(self, address):
        """Return 'libfoo.so+0x123' for a mapped address, else the bare hex."""
        name = self.module_of(address)
        if name is None:
            return "0x%x" % address
        return "%s+0x%x" % (name.rsplit("/", 1)[-1], address - self._bases.get(name, address))

    @property
    def threads(self):
        """Yield (tid, cursig, rip) for every thread in the dump."""
        for ntype, desc in self.notes:
            if ntype != NT_PRSTATUS:
                continue
            cursig = struct.unpack_from("<h", desc, PRSTATUS_CURSIG)[0]
            tid = struct.unpack_from("<i", desc, PRSTATUS_PID)[0]
            rip = struct.unpack_from("<Q", desc, PRSTATUS_REGS + REG_RIP * 8)[0]
            yield tid, cursig, rip

    @property
    def siginfo(self):
        """Return (signo, code, addr) from NT_SIGINFO, or None."""
        for ntype, desc in self.notes:
            if ntype == NT_SIGINFO:
                signo, _, code = struct.unpack_from("<iii", desc, 0)
                return signo, code, struct.unpack_from("<Q", desc, 16)[0]
        return None

    def fault_context(self, stack_pointer):
        """
        Recover the interrupted registers from the signal frame the kernel pushed onto the
        alternate signal stack, so the reported fault is the original one and not the handler's.

        Returns (rip, trapno, cr2) or None.
        """
        i = bisect.bisect_right(self._load_starts, stack_pointer) - 1
        if i < 0:
            return None
        start, end, offset = self._loads[i]
        if not start <= stack_pointer < end:
            return None
        self._file.seek(offset + (stack_pointer - start))
        region = self._file.read(end - stack_pointer)

        for pos in range(0, len(region) - SIGFRAME_GREGS - 23 * 8, 8):
            gregs = struct.unpack_from("<23Q", region, pos + SIGFRAME_GREGS)
            if gregs[GREG_CSGSFS] & 0xFFFF != USER_CS:
                continue
            if gregs[GREG_TRAPNO] not in TRAPS:
                continue
            if self.module_of(gregs[GREG_RIP]) is None:
                continue
            return gregs[GREG_RIP], gregs[GREG_TRAPNO], gregs[GREG_CR2]
        return None


def summarize(path):
    """Return the triage lines for the core dump at path."""
    core = CoreDump(path)
    try:
        return _summarize(core)
    finally:
        core.close()


def _summarize(core):
    threads = list(core.threads)
    lines = ["core %s: %d threads" % (core.path, len(threads))]

    siginfo = core.siginfo
    if siginfo is not None:
        signo, code, addr = siginfo
        lines.append("signal: %s (%d) si_code=%d si_addr=0x%x"
                     % (SIGNALS.get(signo, "signal %d" % signo), signo, code, addr))

    crashed = [t for t in threads if t[1] != 0]
    for tid, cursig, rip in crashed:
        lines.append("crashing thread: tid=%d cursig=%d handling at %s" % (tid, cursig, core.describe(rip)))
        stack = _stack_pointer(core, tid)
        fault = core.fault_context(stack) if stack is not None else None
        if fault is None:
            lines.append("  interrupted context: not recovered — the fault address above is the handler's, not the fault's")
        else:
            fault_rip, trapno, cr2 = fault
            lines.append("  faulted at: %s" % core.describe(fault_rip))
            lines.append("  trap: %d (%s), cr2=0x%x" % (trapno, TRAPS.get(trapno, "unknown"), cr2))
    if not crashed:
        lines.append("crashing thread: none flagged — no thread carried a pending signal")

    lines.append("threads by module:")
    counts = Counter(core.module_of(rip) or "<unmapped>" for _, _, rip in threads)
    for name, count in counts.most_common():
        lines.append("  %4d  %s" % (count, name))

    return lines


def _stack_pointer(core, tid):
    """Return the stack pointer recorded for tid, or None."""
    REG_RSP = 19
    for ntype, desc in core.notes:
        if ntype != NT_PRSTATUS:
            continue
        if struct.unpack_from("<i", desc, PRSTATUS_PID)[0] == tid:
            return struct.unpack_from("<Q", desc, PRSTATUS_REGS + REG_RSP * 8)[0]
    return None
