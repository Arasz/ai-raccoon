#!/usr/bin/env python3
"""Audit memory_search grading coverage — three denominators.

  graded/logged    usefulness != null / grade-log lines        (the naive "12% figure")
  logged/searched  hermes grade lines / ops-log memory_search  (hook capture rate)
  graded/searched  graded lines / ops searches                 (true coverage)

Measured 2026-08-11: 21/172 (12.2%), 168/438 (38%), 20/438 (4.6%) — the naive figure
overstates true coverage ~2.6x because the hook misses most searches.

Usage: python3 audit_coverage.py [grade_log] [ops_log]
Defaults: ~/.ai-badger/memory-grade/memory-quality.jsonl
          ~/.ai-raccoon/memory-operations.jsonl
"""
import collections
import json
import os
import sys


def load(path):
    with open(path) as f:
        return [json.loads(line) for line in f if line.strip()]


grade_path = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser(
    "~/.ai-badger/memory-grade/memory-quality.jsonl")
ops_path = sys.argv[2] if len(sys.argv) > 2 else os.path.expanduser(
    "~/.ai-raccoon/memory-operations.jsonl")

gl = load(grade_path)
searches = [o for o in load(ops_path) if o.get("tool") == "memory_search"]

# Overlap window of both logs (ops log is the hermes-provider channel).
days = sorted(set(l["ts"][:10] for l in gl) & set(o["ts"][:10] for o in searches))
gl_w = [l for l in gl if l["ts"][:10] in days]
hermes = [l for l in gl_w if l.get("host") == "hermes"]
graded_w = [l for l in hermes if l.get("usefulness") is not None]

print(f"grade log: {len(gl)} lines ({gl[0]['ts'][:10]} .. {gl[-1]['ts'][:10]})")
print(f"ops log:   {len(searches)} memory_search ops ({days[0]} .. {days[-1]})")
print()
print(f"graded/logged   : {len(graded_w)}/{len(gl_w)} = {len(graded_w) / len(gl_w) * 100:.1f}%")
print(f"logged/searched : {len(hermes)}/{len(searches)} = {len(hermes) / len(searches) * 100:.0f}%  (hook capture)")
print(f"graded/searched : {len(graded_w)}/{len(searches)} = {len(graded_w) / len(searches) * 100:.1f}%  (true coverage)")

print("\nper-day  searches  logged  capture  graded")
ops_by_day = collections.Counter(o["ts"][:10] for o in searches)
gl_by_day = collections.Counter(l["ts"][:10] for l in hermes)
for d in sorted(set(ops_by_day) | set(gl_by_day)):
    n = ops_by_day.get(d, 0)
    c = gl_by_day.get(d, 0)
    g = sum(1 for l in hermes if l["ts"][:10] == d and l.get("usefulness") is not None)
    cap = f"{c / n * 100:4.0f}%" if n else "  -"
    print(f"{d}  {n:9d}  {c:7d}  {cap}  {g}")

null_proj = sum(1 for l in gl if l.get("projectId") is None)
print(f"\nnull-projectId lines: {null_proj}/{len(gl)} ({null_proj / len(gl) * 100:.0f}%)")
grades = collections.Counter(l["usefulness"] for l in graded_w)
if grades:
    avg = sum(l["usefulness"] for l in graded_w) / len(graded_w)
    print(f"grade dist: {dict(sorted(grades.items()))}  avg {avg:.2f}")
pend = os.path.expanduser("~/.ai-badger/memory-grade/pending.json")
if os.path.exists(pend):
    print(f"pending asks (stash): {len(load(pend))}")
