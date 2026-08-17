# The CLI asks; the server acts

The CLI never writes the bank. It communicates a need to start a job on the server, and the server
does the work — reading directly is permitted where it must, writing never is (ADR-0075). A command
that needs state changed records a request the server picks up; it does not open the bank and change
the state itself, and it does not "start a server, then write anyway."

The sanctioned shape already exists: `model set` routes through the server, which writes an outbox
row, and `ModelMigrationJob` — `Interval => null`, `HasWorkAsync` gated on that row — drains it on
the maintenance loop's on-demand poll. Copy that. A job gated on an explicit request row is not an
unattended job: it runs only because a human asked, which is why `Interval => null` is the load-bearing
part and a clock interval is the thing to avoid.

Two processes writing one SQLite file is the failure this prevents, and it is quieter than it sounds.
`SqliteConnection.ClearPool` is process-local, so a CLI process clearing its own pool does nothing
about the connections the server still holds — the rekey that looked safe is the worked example.

`CliWriteOptOuts` names every exception, and `encryption` is the only one: it creates and keys the
bank before a server can resolve a key and decrypt it. Any addition is an amendment to ADR-0075's
exception table, not a local decision.

**A list nothing checks is not a rule.** `repair reingest --apply` and `repair chunk-index --apply`
wrote directly for months without ever being added to that list, because `IMemoryStore` is bound
unconditionally to the direct store while the routing at `AppRunner` only swaps the settings stores —
so the exception was taken rather than granted, and nothing compared the list against the code. Hold
this invariant with a gate that fails when a command path writes without being sanctioned, or expect
the next command to re-add the hole (see `derive-or-delete-the-list`).

**The read side is gated the same way.** `noise entries` and `watch registered` were reads, so they
never belonged in `CliWriteOptOuts` — but both opened the bank via `OpenBankAsync`, which runs
`MemorySchema.EnsureAsync` and writes migrations on a digest-stale bank, leaving the CLI latent
write capability on the path no list guarded. They now route through the server like `settings
maintenance list`, and `CliCommandsDoNotOpenTheBankTests` walks every CLI command's constructed
graph for a live `ISqliteConnectionFactory`; `BankCapableCliCommandAllowlist` names the three
sanctioned exceptions (`encryption`, `doctor`, `serve`).
