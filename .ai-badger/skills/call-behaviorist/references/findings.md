# What the findings mean

| `kind`                 | Severity | Means                                                                                                                                                                                                   |
|------------------------|----------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `never_observed`       | high     | Registered **and** instrumented, but produced no record while the log holds records from elsewhere. It may never load, or never fire. This is the failure the tool exists to catch.                     |
| `not_instrumented`     | low      | Registered but calls no debug logger, so it *cannot* produce records. Its silence says nothing about health — do not report it as broken.                                                               |
| `version_skew`         | high     | Two versions' observed time ranges **overlap**: two copies were live at once — typically a plugin cache against a `.ai-badger/` scaffold. The finding names each version with the range it was seen in. |
| `always_skipped`       | medium   | Fired every time and exited early every time. Live, but doing nothing.                                                                                                                                  |
| `unexpected_component` | low      | Produced records but is not registered by this project. Often legitimate (a plugin-side hook); worth a glance.                                                                                          |
| `version_unresolvable` | low      | Records carry the `unknown` sentinel: the copy that ran has no VERSION and no manifest above it, so it predates 0.35.4 and needs re-scaffolding.                                                        |
| `version_progression`  | info     | Ran at several versions whose ranges are **disjoint** — an upgrade in sequence. Context, not a fault.                                                                                                   |

`health` is `ok`, `warn`, `degraded`, or **`unknown`**. Treat `unknown` as *nobody looked* — it means there is no evidence, not that everything is fine. Say so plainly in the report rather than implying health. An `info` finding never moves
the verdict: during a release train every component legitimately runs at several versions in turn, and a severity that fires on every ordinary upgrade teaches the reader to skip the one instance that is real.

**Evidence is not the same as lines in the log.** This tool records its own `enabled`,
`disabled` and `cleared` events; those prove the log exists and nothing more. They are excluded from the record count, from `observed`, and from the health verdict. With no evidence,
`never_observed` is withheld too — when nothing at all was observed, every component is trivially silent, and reporting that as a high-severity failure would be crying wolf.
