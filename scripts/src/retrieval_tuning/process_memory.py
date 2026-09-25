"""This process's resident set and physical footprint, current and peak, in KiB (Linux and macOS).

On macOS the footprint is what Activity Monitor shows as Memory; it counts Metal/MLX buffers in
unified memory, which the resident set does not. On Linux the footprint is the resident set.

macOS also reports ANE usage (RUSAGE_INFO_V6's ri_neural_footprint, separate from phys_footprint)
and CPU-only energy (ri_energy_nj); a kernel without V6 falls back to V4, and both fields read 0.
ri_* time fields are mach ticks, not wall time, and are not exposed here.
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
    neural_footprint: int = 0
    neural_footprint_peak: int = 0
    energy_nj: int = 0


_V4_FIELD_NAMES = (
    "user_time", "system_time", "pkg_idle_wkups", "interrupt_wkups", "pageins", "wired_size",
    "resident_size", "phys_footprint", "proc_start_abstime", "proc_exit_abstime",
    "child_user_time", "child_system_time", "child_pkg_idle_wkups", "child_interrupt_wkups",
    "child_pageins", "child_elapsed_abstime", "diskio_bytesread", "diskio_byteswritten",
    "cpu_time_qos_default", "cpu_time_qos_maintenance", "cpu_time_qos_background",
    "cpu_time_qos_utility", "cpu_time_qos_legacy", "cpu_time_qos_user_initiated",
    "cpu_time_qos_user_interactive", "billed_system_time", "serviced_system_time",
    "logical_writes", "lifetime_max_phys_footprint", "instructions", "cycles", "billed_energy",
    "serviced_energy", "interval_max_phys_footprint", "runnable_time",
)

# Fields <sys/resource.h> adds after v4's runnable_time, in struct order, up to v6.
_V6_EXTRA_FIELD_NAMES = (
    "flags", "user_ptime", "system_ptime", "pinstructions", "pcycles", "energy_nj", "penergy_nj",
    "secure_time_in_system", "secure_ptime_in_system", "neural_footprint",
    "lifetime_max_neural_footprint", "interval_max_neural_footprint", "conclave_footprint",
    "page_wait_time_mach", "page_cache_hits",
)


class _RusageInfoV4(ctypes.Structure):
    _fields_ = [("uuid", ctypes.c_uint8 * 16)] + [(name, ctypes.c_uint64) for name in _V4_FIELD_NAMES]


class _RusageInfoV6(ctypes.Structure):
    _fields_ = ([("uuid", ctypes.c_uint8 * 16)]
                + [(name, ctypes.c_uint64) for name in _V4_FIELD_NAMES]
                + [(name, ctypes.c_uint64) for name in _V6_EXTRA_FIELD_NAMES]
                + [("reserved", ctypes.c_uint64 * 6)])


_RUSAGE_INFO_V4 = 4
_RUSAGE_INFO_V6 = 6


def _sample_from_v6(info: "_RusageInfoV6", peak_rss_kib: int) -> MemorySample:
    return MemorySample(info.resident_size // 1024, max(peak_rss_kib, info.resident_size // 1024),
                        info.phys_footprint // 1024, info.lifetime_max_phys_footprint // 1024,
                        info.neural_footprint // 1024, info.lifetime_max_neural_footprint // 1024,
                        info.energy_nj)


def memory_kib() -> MemorySample:
    if sys.platform == "darwin":
        libproc = ctypes.CDLL("/usr/lib/libproc.dylib", use_errno=True)
        peak_rss = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss // 1024  # bytes on macOS

        info6 = _RusageInfoV6()
        if libproc.proc_pid_rusage(os.getpid(), _RUSAGE_INFO_V6, ctypes.byref(info6)) == 0:
            return _sample_from_v6(info6, peak_rss)

        info4 = _RusageInfoV4()
        if libproc.proc_pid_rusage(os.getpid(), _RUSAGE_INFO_V4, ctypes.byref(info4)) != 0:
            raise OSError(ctypes.get_errno(), "proc_pid_rusage failed")
        return MemorySample(info4.resident_size // 1024, max(peak_rss, info4.resident_size // 1024),
                            info4.phys_footprint // 1024, info4.lifetime_max_phys_footprint // 1024)
    status = dict(line.split(":", 1) for line in Path("/proc/self/status").read_text().splitlines() if ":" in line)
    rss, hwm = int(status["VmRSS"].split()[0]), int(status["VmHWM"].split()[0])
    return MemorySample(rss, hwm, rss, hwm)
