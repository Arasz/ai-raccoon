# Follow-up owed on merge (owner instruction, 2026-09-03)

When task `air-search-signal-preservation-stage-one` finishes AND its work is merged
to `main` (Phase 5 merge step complete, merge commit on main verified), send a 1:1
message via the message bus to session:

- **Session ID:** `bee69600-35f0-4af1-9509-962d8b8052e0`

Command (from repo root):

```bash
python3 .ai-badger/skills/send-message/scripts/send_message.py \
  --session-id bee69600-35f0-4af1-9509-962d8b8052e0 \
  --content "<merge commit> on main: <what landed — envelope fields, telemetry, gates> + CI state + anything deferred"
```

Message must state: the merge commit hash, what landed (per-result strength/legs,
response margins, telemetry), CI/gate state, and anything deferred or left open.
Trigger = merge to main, not PR open and not gates-green alone. Do not send twice —
if a send receipt already exists for this event, skip.
