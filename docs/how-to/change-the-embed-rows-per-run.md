# Change the embed drain's rows-per-run

Set how many pending rows one embed-drain pass takes, and know what the number costs you.

---

## The whole path

| Where | What |
|---|---|
| The command | `ai-raccoon settings maintenance embed-rows-per-run <rows>` |
| The setting it writes | `maintenance.embed-rows-per-run.global` |
| Where that lands | the `settings` table of the bank (`memory.db`), one row: `key` holds the setting name, `value` holds the number |
| Who reads it | the embed drain, on every pass, for the memory corpus and the code corpus alike |
| When it takes effect | the drain's next pass. No restart, no reload |
| The accepted range | 64 to 4096, default 128 |

Every link in that table was checked against a running tool rather than read off the source: the
command writes the row, `settings maintenance list` reads the value back, and the row itself is
visible in the bank's `settings` table. The reasoning behind the range is
[ADR-0136](../adr/0136-the-embed-drain-rows-per-run-runs-from-64-to-4096.md).

## Set the value

```bash
ai-raccoon settings maintenance embed-rows-per-run 512
```

The command prints what it stored and exits 0:

```
embed rows per run: 512
```

Both corpora share the one setting. There is no separate value for the code corpus.

Two things the command needs from you. It wants a bank that already exists, because it never
creates one. Point it at the wrong root and it says so instead of quietly starting a bank:

```
ai-raccoon: no bank exists at '/tmp/demo/memory.db' — create it with
'ai-raccoon --data-root /tmp/demo serve', or check --data-root for a typo
```

And the write is routed through the server that owns the data root. The CLI will not fall back to
writing the bank itself, because the server is the only process that writes to it
([ADR-0075](../adr/0075-only-the-server-writes-to-the-bank.md)), so before it writes anything the
CLI has to *prove* the listener on the port serves this data root. When the proof fails, the
command refuses (exit 50) and starts nothing. When the CLI does start a backend for you, it
tells you that the backend keeps running afterwards, under its own idle timeout; stop it with
`ai-raccoon serve --restart --port <port>`.

A value outside the range is refused, and nothing is written:

```
$ ai-raccoon settings maintenance embed-rows-per-run 0
ai-raccoon: embed rows per run must be an integer between 64 and 4096
```

The same message comes back for `63`, for `4097`, and for a value that is not a number at all.

### The exit codes this command can end on

| Exit code | Meaning |
|---|---|
| `10` | `Usage.InvalidValue` — the value is not an integer between 64 and 4096, and nothing was written |
| `31` | `Bank.NoBank` — no bank file exists at the resolved path, so there was nothing to write to |
| `50` | `Server.Unproven` — the listener on the port serves another data root, so the write was not attempted |

The rest of the settings channel's exit codes (the server being unreachable, the token being
refused, the write not landing) are listed once, under
[Who writes to the bank](configure-ai-raccoon-server.md#who-writes-to-the-bank), and they apply
here unchanged.

## Read the value back

```bash
ai-raccoon settings maintenance list
```

```
checkpoint interval: 60 min
vacuum interval: 7 days
embed rows per run: 128
db file: 368.0 KB
wal: 32.2 KB
...
```

`list` (alias `show`) prints the effective value, not the raw row. A bank that never set the
key prints 128, because that is the default the drain uses.

To look at the row itself, open the bank read-only and ask for it:

```sql
SELECT key, value FROM settings WHERE key = 'maintenance.embed-rows-per-run.global';
```

The table is two columns, `key TEXT PRIMARY KEY` and `value TEXT NOT NULL`, so one setting is
one row and a key is written in place. The same key name appears in the drain's warning when a
stored value turns out to be invalid, which is how you find the row when something went wrong.

## Put it back

Writing 128 restores the default behaviour: a value of 128 is exactly what an absent key means,
so the stored value becomes indistinguishable from the default the drain would have used anyway.

```bash
ai-raccoon settings maintenance embed-rows-per-run 128
```

No verb removes the row, and that follows from the shape of the settings tree. Each subsystem's
verbs are specific to it, so `settings access unset` removes an access key and
`settings extract exclude remove` removes an exclude prefix, but nothing removes a maintenance
key. The server does have the route (`DELETE /settings?key=…`, where an absent key and a
removed row mean the same thing), it is simply not exposed for this one.

## What the number buys, and what it costs

It is a pass-size knob, not a throughput knob. The row is the unit of work, so a bigger pass
does not embed a row faster. It buys fewer passes over a backlog, and it pays in memory held
while a pass runs.

| rows-per-run | passes for a 4 914-row backlog | transactions per pass | managed rows held at once (memory corpus) |
|---|---|---|---|
| 64 (floor) | 77 | 2 | about 0.3 MB |
| **128 (default)** | **39** | **4** | **about 0.6 MB** |
| 512 | 10 | 16 | about 2.4 MB |
| 4096 (ceiling) | 2 | 128 | about 19 MB |

The memory column is the one that matters on a small host, and it comes from the way the drain
works: it takes its whole `SELECT … LIMIT` result as a single list before it embeds any of it.
At the measured row size on the live bank (2 325.6 UTF-8 bytes per memory row, and .NET strings
are UTF-16) a 4 096-row pass holds roughly 19 MB of managed memory at once. The code corpus
rows are smaller, about 11 MB at the ceiling.

Raising the value to 512 or 1024 is a reasonable move on a machine with room, and it makes no
difference to the per-row cost. Lowering it to 64 is safe on a memory-constrained host, because
64 is two of the embedder's 32-row sub-batches, and the floor rules out the pathological case
where a pass does one generator call and then pays the fixed per-pass cost again.

## What the value does not change

- **How long one row takes to embed.** The generator call is one row either way, and the row's
  own token count sets its sequence length.
- **The native ONNX peak.** The session shape is one row by the padded length, not N rows by
  the padded length. The 5.9 to 9.5 GB figure in the server memory report is a property of the
  model weights and the 32-row database batch, not of this setting.
- **The transaction scope.** A row is still marked inside a 32-row transaction, and a remote
  provider still receives 32 rows per request.

## If the stored value is out of range

The command refuses to write an invalid value, so a row outside 64 to 4096 can only arrive from
an older tool, a hand-edited bank, or a setting written by something else entirely. The drain
does not fail on it. It clamps the value to the nearest bound (or falls back to the default for
a value that is not a number) and logs a warning, once per distinct bad value:

```
Invalid maintenance.embed-rows-per-run.global setting '32': expected an integer between 64
and 4096. Falling back to the default or clamped value.
```

That warning carries event id 1006, and the setting name in it is the same one the command
writes and the same one you query in the `settings` table. The stored value is left as it is.
Correct it with the command above.

## See also

- [ADR-0136](../adr/0136-the-embed-drain-rows-per-run-runs-from-64-to-4096.md) — the range, the
  mechanics of one pass, and the measured anchors behind the table.
- [CLI reference](../reference/cli-reference.md) — every `settings` verb in one table.
- [Configure the AiRaccoon server](configure-ai-raccoon-server.md) — the data root, the bank, and
  the passphrase-encrypted bank.
