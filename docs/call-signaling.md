# Call signaling

Call signaling is REST-only in the v1 server. The WebSocket gateway remains
push-only: each authenticated call action validates membership, advances the
in-memory state machine, and publishes one participant-scoped gateway event.

The active call registry is a singleton keyed by conversation ID. This is an
intentional single-server v1 limitation; distributed deployments need shared
call state before they can safely accept signaling on multiple instances.

The five routes are:

- `POST /conversations/{id}/call/ring`
- `POST /conversations/{id}/call/offer`
- `POST /conversations/{id}/call/answer`
- `POST /conversations/{id}/call/ice`
- `POST /conversations/{id}/call/hangup`

Each `sdp`, `candidate`, and `reason` field is limited to 64 KiB of UTF-8
payload data. Invalid transitions return `409 call_invalid_state` and do not
publish an event.
