# Server hardening

The message, GIF search, and conversation call POST endpoints use a fixed-window
per-user limiter. `RateLimit:Limit` defaults to 30 and
`RateLimit:WindowSeconds` defaults to 60. Rejected requests return `429` with a
`rate_limited` JSON error and `Retry-After` seconds.

The gateway accepts WebSocket requests without an `Origin` header. When
`Gateway:AllowedOrigins` is empty (the development-friendly default), any present
origin is accepted. Set it to a comma-separated list of exact origins to reject
untrusted browser upgrades with `403`.
