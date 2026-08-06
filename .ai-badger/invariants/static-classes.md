# Static classes: extensions, constants, and pure functions only

Static classes are allowed for extensions, constants, and pure functions — no state,
no I/O, no injectable dependencies. Anything with state, I/O, or dependencies is an
injectable component (constructor injection; see `Setup/Cli/Commands/` for the pattern
— `IEncryptionCommands`/`EncryptionCommands`, Part 1).

The one sanctioned exception is the `ConfigCommands` static dispatcher (optional
interface params + `?? ThrowHelper.ThrowArgumentNullException<T>()`), which exists to
cap test churn across the family test files; it is a dispatcher, not a logic holder.

Rationale and the full classification table: `docs/work/2026-08-06-static-class-classification.md`.
