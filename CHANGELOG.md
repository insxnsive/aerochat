# Release notes

## Unreleased

### Client and server compatibility

Clients that use call generation IDs need a server that includes commit
`cc18909a86bfbdeaedd99801d78d4c9bc7781319` or a commit built on it. That change adds
call IDs to call responses and gateway signals, and checks the ID on later call
actions. Older servers do not send call IDs.

Commit `e648787c55448cd15be9764a24a449eaf65601fc` changes how the gateway
authenticates. The client sends `Authorization: Bearer <session token>` on the
WebSocket upgrade. The server refuses any `token` query key on `/ws` with HTTP 400
`invalid_request`, even when the request also has a valid Authorization header. The
gateway URL can still carry `lastEventId` to resume.

Upgrade the client and the server together. An older client that sends `?token=`
gets 400 from the new server, and the new client gets 401 from an older server. A
server for these clients needs both changes.

There is no numbered release yet, so this note names commits instead of a version.

Sources:
- [Server call ID contract](https://github.com/insxnsive/aerochat/commit/cc18909a86bfbdeaedd99801d78d4c9bc7781319)
- [Gateway header authentication](https://github.com/insxnsive/aerochat/commit/e648787c55448cd15be9764a24a449eaf65601fc)
