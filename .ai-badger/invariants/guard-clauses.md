# Guard clauses over hand-rolled null checks

Prefer a dedicated guard/throw-helper for argument validation over hand-rolled `x ?? throw ...`
or ad hoc `if (x == null) throw` blocks — a guard reads as intent, not boilerplate, and keeps
the exception type/message consistent across the codebase. Use the idiomatic guard utility for
the language/stack in use, and fail fast at the boundary rather than letting invalid state flow in.
