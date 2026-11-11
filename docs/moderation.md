# Moderation foundation

Operators are set with `Moderation:OperatorIds`, an array of existing local user GUIDs, in deployment configuration (for example `Moderation__OperatorIds__0`). An empty list means nobody has operator rights, and a role the client claims never makes anyone an operator. If the configuration is invalid, startup fails with a fixed error that doesn't repeat the input.

`Moderation:AuditRetentionDays` defaults to 90. The setting is recorded, but nothing purges audit rows yet. SQLite triggers reject ordinary updates and deletes on the audit table, although anyone who controls the database can still change its schema.

So far the server stores audit records and persistent server bans. It has no moderation endpoints and doesn't enforce bans. Later changes add message tombstones that can't be undone, kicks from group and channel conversations, server-wide ban and unban, and revocation of existing sessions when a change commits. DMs can't be kicked, and someone who was kicked can be added back. Personal contact blocking is a separate feature. Content that was already delivered and TURN allocations that were already handed out can't be recalled.

A moderation change must stage the state change and exactly one audit row in the same explicit database transaction, save, and then commit. Retries, no-op changes and rejected requests add no audit rows. An audit row holds the actor, action, target, conversation, time and reason code, and never message content, credentials or headers. The allowed reason codes are `spam`, `abuse` and `policy`. A later endpoint will let operators read the audit log one page at a time.

Each operator gets 30 write attempts per 60 seconds, counted per process and separate from the message and TURN limits. Future endpoints will answer a denied attempt with 429 `rate_limited` and a `Retry-After` header. The quota helper doesn't enforce anything at an endpoint on its own.
