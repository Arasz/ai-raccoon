---
id: 10
title: "Every Check Must Be Able to Fail"
slug: every-check-must-be-able-to-fail
publishedAt: "2026-07-29"
author: "Rafał Araszkiewicz"
description: "A release guard passed 32 times in a row while being structurally incapable of failing. Four of my checks had the same defect, and every one was caught by a human noticing something downstream — never by a test. So I built a test that provokes each check into failing, and refuses to let a new one exist without a proven failure path."
tags: [Testing, Python, Quality, Mutation Testing, Tooling, CI]
status: draft
categories: [verification]
---

## A green check that could not have been anything else

My release guard compares the working tree against the last release tag: if a shipped surface
changed, `VERSION` must have been bumped. It passed 32 consecutive times.

It was, throughout, incapable of failing. It derived its baseline from the last tag — and the last
tag was eighteen minor versions stale. Against a baseline that old it *always* found changes and
*always* found a differing `VERSION`. One answer, every time, and that answer looked exactly like
success.

Nothing in the output distinguished the eight days it was broken from the eight days it worked. A
check whose verdict cannot vary is not a weak check. It is a decoration that consumes CI minutes.

## The same defect, four times

Once I had a name for it, I went looking. This is what I found in my own repository:

| Where                          | The degeneracy                                                                                     |
|--------------------------------|----------------------------------------------------------------------------------------------------|
| `release_guard` (0.21.0)       | Baseline 18 minors stale — always found changes *and* a bump, so it passed 32 releases              |
| `release_guard` (0.35.3)       | **Detected** untagged releases, printed `UNTAGGED RELEASES`, and returned `0`                       |
| `is_instrumented()` (0.34.0)   | Read a command string still containing the literal `${CLAUDE_PROJECT_DIR}`, so every read failed and every hook reported "not instrumented" |
| `drift.compare()` (#110)       | Hashed the scaffolded copy against the hash recorded when that copy was written — both sides the project's own state, so they always matched |

Two different failure modes hide in that list. The first and last are checks that always return
*pass*. The middle one always returns *fail* — every hook reported "not instrumented", which is just
as uninformative and considerably more annoying. The third reports a real finding and then exits
zero, which is the purest form: it *knows*, and it doesn't act.

The unifying property is not "pass" or "fail". It is **the verdict cannot vary with the input**.

And the detail that actually stung: every single one was found by a human noticing something odd
downstream. Never by a test. My test suite had a hole shaped exactly like my test suite.

## Provoke the check into failing

The fix is a module that does to my checks what a mutation tester does to production code: build a
known-bad state, run the real check against it, and demand that it fails.

Each check gets a `Provocation` — a fixture builder plus the failure it must produce:

```python
class Provocation(NamedTuple):
    """A known-bad state for one check, and the failure it must produce."""
    check: str
    label: str
    run: Callable[[Path, bool], Outcome]
    signal: Signal
```

The fixture builder takes a `provoked` flag and builds *the same fixture either way* — with the
defect when provoked, without it otherwise. Here is the one that reproduces the original 32-release
failure:

```python
def _release_guard_no_bump(work: Path, provoked: bool) -> Outcome:
    """A shipped-surface change with no VERSION bump — the F-11 case."""
    repo = _repo(work / "repo")
    _write(repo / "VERSION", "0.1.0\n")
    _write(repo / "skills" / "a.md", "a\n")
    _commit(repo, "release 0.1.0")
    _git(repo, "tag", "ai-badger--v0.1.0")

    _write(repo / "skills" / "a.md", "changed\n")
    if not provoked:
        _write(repo / "VERSION", "0.2.0\n")
    _commit(repo, "tweak a skill")
    return _run_gate("gates/release_guard.py", "--root", str(repo))
```

A real git repository under `tmp_path`, a real tag, a real commit, and the gate invoked as a
**subprocess** — because this defect class lives in the wiring at least as often as in the logic. A
gate that works when imported and dies when executed is exactly the kind of thing that passes a unit
test and protects nothing.

## Both answers, or the provocation proves nothing

Provoking a failure is only half of it. Every provocation is run twice, and the second run is the
one that carries the weight:

```python
@pytest.mark.parametrize("provocation", REGISTRY, ids=_IDS)
def test_check_holds_its_verdict_on_the_same_fixture_without_the_defect(provocation, tmp_path):
    """The other answer. A provocation whose fixture fails clean too proves nothing."""
    outcome = provocation.run(tmp_path, False)

    if provocation.signal.exit_code is not None:
        assert outcome.exit_code == 0, ...
    assert provocation.signal.unmet(outcome), ...
```

Without this, a provocation that fails for a *setup* reason — a missing binary, a malformed fixture,
a typo in a path — would sail through as proof. The check would "fail when provoked" for reasons
entirely unrelated to the defect, and I would have built a degenerate test to guard against
degenerate checks.

That symmetry is the whole idea. A check must produce **both** answers on the **same** fixture, with
the only difference being the defect itself.

> **The general rule.** A test that can only pass tells you nothing about your code. A test that can
> only fail tells you nothing either. Demonstrated variability is the property that makes a verdict
> mean something — and it is almost never asserted.

## Discovery is mechanical, so new checks cannot slip through

A registry that humans maintain drifts the moment someone adds a gate and forgets. So the module
finds the checks itself:

```python
def discovered_checks(root: Path) -> Dict[str, str]:
    """Every check in this repo, keyed by id, valued by how it was found."""
    found = {}
    for gate in sorted((root / "gates").glob("*.py")):
        found[f"gates/{gate.name}"] = "a script in gates/"
    for rel in _tracked_sources(root):
        text = (root / rel).read_text(encoding="utf-8", errors="replace")
        if _declares_check_flag(text):
            found[f"{rel} --check"] = "declares a --check mode"
        if _defines_compare(text, rel):
            found[f"{rel}::compare"] = "defines a top-level compare()"
    ...
```

Five discovery routes: a script in `gates/`, a module declaring a `--check` flag, a module
declaring an `--all` flag, a top-level `compare()`, and — parsed out of the source with `ast` —
every finding kind the analyzer can emit.
Anything discovery finds that has neither a provocation nor an exemption **fails the build**:

```python
def test_every_check_has_a_provocation(root):
    """A guard with no proven failure path is a guard nobody has shown can fail."""
    registered = {p.check for p in REGISTRY}
    unproven = sorted(check for check in discovered_checks(root)
                      if check not in registered and check not in EXEMPTIONS)
    assert not unproven, ...
```

The reverse holds too: `test_registry_names_only_checks_that_exist` fails if the registry names a
check that has been deleted, because a provocation for something that no longer exists proves
nothing about the tree as it is today.

## Exemptions are debts, not decisions

There is one escape hatch, and it is deliberately uncomfortable:

```python
# Checks with no provocation yet. EVERY ENTRY IS A DEBT, not a decision: an empty-by-default
# list that only grows is worthless. Delete an entry by writing its provocation.
EXEMPTIONS: Dict[str, str] = {
    f"{DRIFT}::compare": "covered by #110's own framework-mutation test; register once that lands",
}
```

A separate test asserts every exemption names a **live** check, has **no** provocation already, and
carries a **non-empty reason**. Stale exemptions cannot accumulate quietly, which is the usual fate
of allow-lists: they start as "temporary" and become the architecture.

One entry today. The list is meant to shrink.

## The relapse surface is one layer out

The last test in the module doesn't test a check at all. It tests the workflows that run one:

```python
def test_every_workflow_running_a_gate_checks_out_deep_enough(root):
    """F-11's relapse surface: `fetch-depth: 1` fetches no tags, so release_guard finds none."""
```

`actions/checkout` defaults to `fetch-depth: 1`, which fetches **no tags**. The release guard, finding
no tag, takes its documented always-pass path — and the entire gate becomes a silent no-op again,
without a single line of its own code changing. Same defect, one layer out, reachable by a
well-meaning workflow edit.

There is a small detail in that test worth stealing. It parses each line and strips comments before
looking for the setting:

```python
settings = [line.split("#", 1)[0].strip() for line in text.splitlines()]
if "fetch-depth: 0" not in settings:
```

Because the comment *above* that very setting explains the hazard by naming `fetch-depth: 1` — and a
naive substring search would read the warning as the answer. A check that can be satisfied by a
comment about the check is, once again, a check that cannot fail.

## What this does not prove

It would be a poor article on degenerate verification that overstated its own guarantees.

- **It does not prove a check is correct.** It proves the check can produce both verdicts on one
  fixture. A gate that fires on the wrong condition passes this suite happily.
- **The provocation is my model of the defect.** If I misunderstand what a check should catch, I
  encode that misunderstanding into the fixture, and it agrees with me. (I have made exactly this
  mistake in an outage post-mortem, tuning a reproduction until it confirmed my suspicion.)
- **Coverage is bounded by discovery.** Four routes find four kinds of check. A validation written
  somewhere none of them look is invisible — not exempted, not flagged, simply unseen.

What it does buy is narrow and real: no check in this repository can exist without someone having
demonstrated, in a subprocess, on a real fixture, that it is able to say no. The four defects at the
top of this article would all have been caught on the day they were introduced instead of weeks
later by a human squinting at something downstream.

The file was 668 lines for 19 provocations when this was drafted. Checked again on 2026-08-15, it had
grown past 1,100 lines and past thirty provocations, and it is still growing. Read any number here
as a snapshot, not a ceiling. That is a lot of test for a small tool, and I would write it again.
Every one of those provocations is a bug that already happened once.

The whole module is public, MIT, and readable end to end:
[`tests/test_every_check_can_fail.py`](https://github.com/Arasz/ai-badger/blob/main/tests/test_every_check_can_fail.py).
