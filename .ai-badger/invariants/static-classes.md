# Static classes: extensions, constants, and pure functions only

Static classes are allowed for extensions, constants, and pure functions — no state,
no I/O, no injectable dependencies. Anything with state, I/O, or dependencies is an
injectable component (constructor injection, interface + implementation pair), not a
static class. A static dispatcher with optional interface parameters is the one
sanctioned exception: it exists to cap test churn, and it stays a dispatcher, never a
logic holder.
