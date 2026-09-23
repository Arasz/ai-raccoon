"""Fixture data, not tests: stop pytest from collecting anything under here.

A fixture corpus can legitimately contain files pytest's default discovery
would otherwise treat as test modules (e.g. code_eval/corpus/test_greeter.py,
a fixture SOURCE file some other test parses/copies, not a suite to run).
"""

collect_ignore_glob = ["*"]
