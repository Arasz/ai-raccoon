# Static classes: extensions and constants only

Static classes are allowed for extension methods and constants. Everything else is an
injectable component (constructor injection, interface + implementation pair).

"It is a pure function" justifies a static *helper* — a small calculation over its
arguments with no domain role, like math, string formatting or path joining. It does not
justify a **component**: a named thing that does a job in the system. If it reads like a
noun with a role — Reader, Parser, Planner, Validator, Serializer, Factory, Builder,
Probe, Resolver — it is a component, and purity is irrelevant. `OnnxGraphProbeReader` is
a component, not a pure function.

Two questions settle it: would a test ever want to substitute this, and could there
plausibly be a second implementation? Either "yes" means injectable.

State, I/O, or dependencies make it injectable regardless — a static class never touches
the filesystem, the clock, the network, or the bank.

Sanctioned exceptions, both narrow: a static dispatcher with optional interface
parameters (it exists to cap test churn, and stays a dispatcher, never a logic holder),
and the nested `static partial class Log` holding `[LoggerMessage]` methods, which the
source generator requires — see `high-performance-logging.md`.
