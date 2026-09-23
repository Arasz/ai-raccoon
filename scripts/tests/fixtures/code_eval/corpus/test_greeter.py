"""Tests for greeter.py (E3 fixture corpus: python/small, src/test pair)."""

from greeter import Greeter, greet_all


def test_greet_increments_the_count():
    greeter = Greeter()
    assert greeter.greet("Ada") == "Hello, Ada! (#1)"
    assert greeter.greet("Grace") == "Hello, Grace! (#2)"


def test_reset_zeroes_the_count():
    greeter = Greeter()
    greeter.greet("Ada")
    greeter.reset()
    assert greeter.count == 0


def test_greet_all_shares_one_counter_across_names():
    results = greet_all(["Ada", "Grace"], salutation="Hi")
    assert results == ["Hi, Ada! (#1)", "Hi, Grace! (#2)"]
