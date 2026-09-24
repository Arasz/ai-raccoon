"""This process's resident set and physical footprint, current and peak, in KiB (Linux and macOS).

On macOS the footprint is what Activity Monitor shows as Memory; it counts Metal/MLX buffers in
unified memory, which the resident set does not. On Linux the footprint is the resident set.
"""

from __future__ import annotations

import ctypes
import os
import resource
import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class MemorySample:
    rss: int
    rss_peak: int
    footprint: int
    footprint_peak: int


class _RusageInfoV4(ctypes.Structure):
    _fields_ = [("uuid", ctypes.c_uint8 * 16)] + [(name, ctypes.c_uint64) for name in (
        "user_time", "system_time", "pkg_idle_wkups", "interrupt_wkups", "pageins", "wired_size",
        "resident_size", "phys_footprint", "proc_start_abstime", "proc_exit_abstime",
        "child_user_time", "child_system_time", "child_pkg_idle_wkups", "child_interrupt_wkups",
        "child_pageins", "child_elapsed_abstime", "diskio_bytesread", "diskio_byteswritten",
        "cpu_time_qos_default", "cpu_time_qos_maintenance", "cpu_time_qos_background",
        "cpu_time_qos_utility", "cpu_time_qos_legacy", "cpu_time_qos_user_initiated",
        "cpu_time_qos_user_interactive", "billed_system_time", "serviced_system_time",
        "logical_writes", "lifetime_max_phys_footprint", "instructions", "cycles", "billed_energy",
        "serviced_energy", "interval_max_phys_footprint", "runnable_time")]


_RUSAGE_INFO_V4 = 4


def memory_kib() -> MemorySample:
    if sys.platform == "darwin":
        info = _RusageInfoV4()
        libproc = ctypes.CDLL("/usr/lib/libproc.dylib")
        if libproc.proc_pid_rusage(os.getpid(), _RUSAGE_INFO_V4, ctypes.byref(info)) != 0:
            raise OSError(ctypes.get_errno(), "proc_pid_rusage failed")
        peak_rss = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss // 1024  # bytes on macOS
        return MemorySample(info.resident_size // 1024, max(peak_rss, info.resident_size // 1024),
                            info.phys_footprint // 1024, info.lifetime_max_phys_footprint // 1024)
    status = dict(line.split(":", 1) for line in Path("/proc/self/status").read_text().splitlines() if ":" in line)
    rss, hwm = int(status["VmRSS"].split()[0]), int(status["VmHWM"].split()[0])
    return MemorySample(rss, hwm, rss, hwm)
