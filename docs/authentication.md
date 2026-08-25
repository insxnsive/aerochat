# Authentication Architecture

Aerochat uses provider-backed accounts and never stores passwords. The server currently supports Google, GitHub, and Discord through OAuth 2.0 Authorization Code with S256 PKCE.

## Account identity

The stable account key is `(provider, provider user ID)`.

Email is optional profile metadata, not an account key or login requirement. The server stores an email only when the provider response explicitly marks it verified. Missing or unverified email is represented as `null`.

GitHub's normal `/user` response does not establish email verification, so Aerochat deliberately stores no GitHub email in v1. A future `/user/emails` integration may add verified-email metadata without changing account identity.

## Desktop login flow

1. The desktop client starts a loopback listener.
2. It opens `GET /auth/{provider}/start?returnUri=<loopback callback>` in the user's browser.
3. The server accepts only numeric HTTP loopback return URIs using `127.0.0.1` or `[::1]`. Userinfo, query strings, fragments, host aliases, and non-loopback addresses are rejected.
4. The server creates a random authorization state and PKCE verifier. State expires after 10 minutes.
5. The browser is redirected to the provider with the fixed server callback `/auth/{provider}/callback`, the random state, and an S256 code challenge. The verifier and provider client secret never enter the authorization URL.
6. The callback consumes state before any downstream processing, verifies that the callback provider matches the state, exchanges the provider code, fetches provider identity, and upserts the local external user.
7. The callback never puts the Aerochat session token in a URL. It creates a random one-time handoff code, valid for 60 seconds, and redirects to the validated desktop loopback URI with only that handoff code.
8. The client posts the handoff code to `POST /auth/session/exchange`. The server consumes it and returns `{ accessToken, expiresIn }` as JSON.

State and handoff codes are destructive reads: every callback/exchange attempt is single-use, including malformed or provider-failed attempts. This is intentional replay prevention.

## Session tokens

`SessionService` issues one-hour HS256 JWTs with:

- issuer `aerochat-server`
- subject `<provider>:<provider user ID>`
- provider
- provider user ID
- display name
- issued-at and expiration timestamps

Signing keys must be at least 32 bytes. `Auth:SessionSigningKey` is base64-encoded. If no key is configured, the server generates an ephemeral key for that process; all sessions then reset when the process restarts.

## Server configuration

Provider secrets and deployment configuration never enter source control.

Configuration keys:

```text
PublicBaseUrl
Auth:SessionSigningKey
Auth:Google:ClientId
Auth:Google:ClientSecret
Auth:GitHub:ClientId
Auth:GitHub:ClientSecret
Auth:Discord:ClientId
Auth:Discord:ClientSecret
```

Environment-variable form uses double underscores, for example:

```text
Auth__Google__ClientId
Auth__Google__ClientSecret
Auth__SessionSigningKey
```

A provider with missing credentials returns HTTP 503 `provider_not_configured`; it does not prevent `/health` or other configured providers from running.

## Resource bounds

Pending authorization state and handoff stores are process-local and bounded (4096 entries each by default). Creation is serialized per store so concurrent requests cannot exceed the cap. When capacity is reached, expired entries are reclaimed before rejecting new work. Remaining exhaustion returns HTTP 503 `oauth_capacity` rather than allowing unbounded memory growth.

Authentication start endpoints still require rate limiting before public production deployment; rate limits are tracked in the server-hardening phase.

## Source map

```text
Aerochat.Server/Auth/SessionService.cs
Aerochat.Server/Auth/OAuth/OAuthEndpoints.cs
Aerochat.Server/Auth/OAuth/OAuthFlowService.cs
Aerochat.Server/Auth/OAuth/OAuthFlowStore.cs
Aerochat.Server/Auth/OAuth/OAuthProviderClient.cs
Aerochat.Server/Auth/OAuth/InMemoryExternalUserStore.cs
Aerochat.Server.Tests/SessionServiceTests.cs
Aerochat.Server.Tests/OAuthFlowTests.cs
```

## Verification

Run from the repository root using x64:

```bash
dotnet test Aerochat.Server.Tests/Aerochat.Server.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet test Aerochat.sln -c Debug -p:Platform=x64 --no-restore
dotnet build Aerochat.sln -c Debug -p:Platform=x64 --no-restore
```

The OAuth tests cover loopback validation, PKCE secrecy, verified-email handling, stable identity upserts, malformed callbacks, state/handoff single use and expiry, capacity bounds, token tampering, token expiry, and signing-key requirements.
