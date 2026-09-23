"""A tiny greeting module (E3 fixture corpus: python/small)."""

from __future__ import annotations


class Greeter:
    """Greets a name, remembering how many times it has greeted so far."""

    def __init__(self, salutation: str = "Hello") -> None:
        self.salutation = salutation
        self.count = 0

    def greet(self, name: str) -> str:
        self.count += 1
        return f"{self.salutation}, {name}! (#{self.count})"

    def reset(self) -> None:
        self.count = 0


def greet_all(names: list[str], salutation: str = "Hello") -> list[str]:
    """Greets every name in order with a single shared Greeter instance."""
    greeter = Greeter(salutation)
    return [greeter.greet(name) for name in names]
