# Server status — why a silent server is still in the index

Each `sources[]` entry records what the host's last listing supported. A zero-tool server used to be dropped at write time, which made "switched off" and "running but exposing nothing" the same absence with opposite remedies (ADR-0014
decision 7).

| `status`           | what the listing said                                                                            | remedy                                       |
|--------------------|--------------------------------------------------------------------------------------------------|----------------------------------------------|
| `ok`               | the server reported tools                                                                        | none                                         |
| `disabled`         | the host says the server is switched off                                                         | enable it (`hermes mcp configure`)           |
| `empty`            | enabled, asked, exposed nothing                                                                  | check the server actually starts             |
| `unknown`          | the listing carried no tool detail at all                                                        | see below                                    |
| `absent`           | the host no longer lists the server; its tools are marked `removed`                              | re-add it, or accept the removal             |
| `unauthenticated`  | `claude mcp list` said `! Needs authentication`                                                  | `claude mcp login <server>`                  |
| `unreachable`      | `claude mcp list` said `✘ Failed to connect`                                                    | fix the endpoint or the credentials it names |
| `pending_approval` | `claude mcp list` said `⏸ Pending approval` — an unapproved `.mcp.json` server, not connected to | approve it in the host                       |

`unknown` is the honest reading of a listing with no tool detail: the `hermes mcp list` text table (its Tools column reads `all`, never the tool names) and a `claude mcp list` server that is merely
`✔ Connected`. An `update` over such a listing restates statuses and **does not** mark anything removed — "not asked" is not "exposes nothing".

The status enum is additive, and a status is added only once a host CLI is *observed* reporting it:
the four phrases above are what `claude mcp list` produced on a 2026-07 install and what its own
`--help` documents. Its other documented phrases (`Connection error`, `Rejected`, `not configured`)
are not mapped, and a phrase this release does not know reads as `unknown` rather than inventing a distinction the data cannot support.
