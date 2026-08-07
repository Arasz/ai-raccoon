# Observability checklist (4.3)

- [ ] **A renamed or removed metric, label, or structured-log field is treated
  as a breaking change** — operators' dashboards, alerts and saved queries bind
  to those names, so they are a quasi-API. A rename in the diff gets the same
  question as an API rename: who consumes it, and what changes with it?
- [ ] **Trace context crosses every async boundary the change adds** — a queue
  publish/consume, a thread-pool or executor hop, a background task, a
  fire-and-forget call. Working HTTP tracing is not evidence that these work;
  propagation breaks *silently* and surfaces months later as an orphaned span.
- [ ] **Readiness does not depend on optional downstreams** — a probe that fails
  when a non-essential dependency degrades pulls the instance out of rotation for
  a fault it could have served through. Liveness, readiness and startup answer
  three different questions.
- [ ] **Every new metric has a named consumer** — the alert, dashboard, or
  investigation it exists for. A metric that drives none of the three is cost and
  noise, not observability.
- [ ] **Histogram buckets reflect the latency objective** — library-default
  buckets usually straddle the threshold that matters, which makes the percentile
  at that threshold unreadable exactly when someone needs it.

> Distilled from the Kotlin `observability-integrator` skill
> ([Kotlin/kotlin-backend-agent-skills](https://github.com/Kotlin/kotlin-backend-agent-skills));
> no licence file accompanied the captured copy, so its terms are unestablished —
> these are restatements of the underlying operational rules, not copied text.
