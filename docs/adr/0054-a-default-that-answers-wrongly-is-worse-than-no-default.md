# 0054. A default that answers wrongly is worse than no default

Date: 2026-08-15

Status: Accepted

## Context

Three port members shipped default interface implementations whose fallback behaviour was not merely
incomplete but **wrong**:

| Member | Default | What the default did |
|---|---|---|
| `IMemoryStore.GetAsync` | `Task.FromResult<MemoryEntry?>(null)` | reported **"not found"** for an entry that exists |
| `IMemoryStore.DeleteInScopeAsync` | `DeleteAsync(projectId, hash, ct)` | **widened a scoped delete into an unscoped one** — the exact reach the member exists to prevent |
| `IPromotionQueueStore.ClaimAsync` | `DiscardAsync(...).SingleOrDefault()` | **deleted the candidate** instead of claiming it |

Each carried the same justification in its doc comment: *"so an implementation predating this
[member] needs no change."* That reasoning protects an implementor from a compile error by handing it
a silent wrong answer instead — and a compile error is exactly how an implementor should be told it is
missing a member.

The 2026-08-14 project-scope review's architecture lane filed this (F7), noting there is **one**
production implementation of each, so the compatibility the defaults bought was never needed by
production code at all.

## Decision

**All three are abstract.** An implementor that does not supply them does not compile.

The compiler then named every implementor that had been relying on a default. There were **six, all
test fakes, none in production** — confirming the lane's reading. Each now declares the member
**explicitly**, forwarding to exactly what the default did:

- `FakeMemoryStore.DeleteInScopeAsync` → `DeleteAsync`. Its own remarks previously said the member was *"deliberately not declared here; the interface's own default delegates it to DeleteAsync, so overriding that member covers both."* The fake was leaning on the wrong default and documenting that it did.
- Four `IPromotionQueueStore` fakes → their own `DiscardAsync`.

**Behaviour is unchanged everywhere** — 2,147 tests pass with no edits beyond the declarations. The
difference is where the simplification lives: **a fake may simplify; a port may not lie.** A fake that
forwards a scoped delete to an unscoped one is a stated shortcut in one test double. A *port* that
does it is a trap for every future implementor, and it silently mis-answers on a path — `memory_get`,
the sweep's scoped delete, a promotion claim — where the wrong answer is indistinguishable from a
right one.

## Consequences

- Adding an `IMemoryStore` or `IPromotionQueueStore` implementation now fails to build until every member is supplied, rather than compiling and answering wrongly.
- Six test fakes gained an explicit member and a comment saying why it is theirs to choose.
- No production behaviour changed; there was only ever one production implementation of each.

## Evidence

The gate here is the **compiler**, so the demonstration is a build, not a test run. A stub implementor
that omits the members was rejected:

```
CS0535 'ProbeForgetfulStore' does not implement interface member
       'IMemoryStore.DeleteInScopeAsync(string, string, string, CancellationToken)'
```

Before this change, that member — and `GetAsync` — were absent from the compiler's list, silently
satisfied by defaults that answered wrongly. `Speed=Fast` is green at 2,147 passed / 0 failed both
before and after, which is the point: this removes a hazard without moving behaviour.
