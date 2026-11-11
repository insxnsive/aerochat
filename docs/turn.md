# TURN credentials and coturn

This server uses coturn's REST shared-secret scheme. Task 16 is not present in the
tracked visual-only plan. The API and defaults here were decided for B-T16-TURN.
Client integration is a separate A-T16-CLIENT slice. Nothing here connects the
current client to TURN or proves that media relay works.

## Server configuration

Supply settings through the normal .NET configuration providers. Example variable
names and placeholder values only:

```text
Turn__Urls__0=turn:relay.example.invalid:3478?transport=udp
Turn__Urls__1=turns:relay.example.invalid:5349?transport=tcp
Turn__SharedSecret=<externally-provisioned-shared-secret>
Turn__CredentialTtlSeconds=3600
Turn__RateLimit__Limit=6
Turn__RateLimit__WindowSeconds=60
```

Provision a cryptographically random secret of at least 32 random bytes, encoded
as a printable string, through a deployment secret provider or the environment.
Both servers must use the identical string. HMAC uses its UTF-8 bytes; the
application does not Base64-decode the secret. Never reuse Auth:SessionSigningKey,
commit a real secret to the repository, or include it in logs, error messages or
command history. The app rejects a secret shorter than 32 UTF-8 bytes, one that
contains control characters, and exact reuse of the configured session signing
key. These checks do not measure entropy.

Turn:Urls is an array of turn: or turns: URIs, not an HTTP URL or a scalar string.
Hostnames, IPv4 and bracketed IPv6 are supported. Ports are 1 to 65535 and the
optional transport is udp or tcp. Userinfo, paths, fragments and other query
parameters are rejected. TTL defaults to 3600 seconds and accepts 60 to 86400
inclusive. The independent TURN rate limit defaults to 6 requests per 60 seconds
per authenticated local user per server process. Limit accepts 1 to 1000;
WindowSeconds accepts 1 to 86400. These quotas are per process, not a
deployment-wide limit across replicas.

With both URLs and secret absent, TURN is disabled and the authenticated endpoint
returns an empty list. Partial or invalid configuration prevents startup with a
sanitized error. Valid TTL or quota tuning alone does not enable TURN.

## API

GET /turn/credentials requires the existing session Authorization: Bearer header
and a matching local user. Missing, invalid or expired sessions and unknown users
receive 401 with WWW-Authenticate: Bearer. Every response from this endpoint uses
Cache-Control: no-store. Do not log response bodies that contain temporary
credentials.

Configured response shape, with placeholder values:

```json
{"iceServers":[{"urls":["turn:relay.example.invalid:3478?transport=udp"],"username":"<expiryUnixSeconds>:<localUserGuidN>","credential":"<Base64-HMAC-SHA1>"}],"ttl":3600}
```

Disabled response: `{"iceServers":[],"ttl":0}`. The seventh request in a default
window receives 429 `{"error":"rate_limited"}` with Retry-After seconds. Different
local users have independent quotas; message/call quotas are separate.

The username expires at floor(current UTC Unix seconds) plus ttl and includes the
local user GUID in N format. The credential is Base64(HMAC-SHA1(shared secret,
username)), using UTF-8 text bytes. A client will need to fetch fresh credentials
before the current ones expire. This slice implements no client refresh and makes
no promise about a call that outlives its credentials.

## coturn setup

Provision coturn separately; this slice neither installs nor starts it. A minimal
configuration excerpt, using placeholders only:

```text
use-auth-secret
static-auth-secret=<the-identical-externally-provisioned-shared-secret>
realm=relay.example.invalid
listening-port=3478
tls-listening-port=5349
cert=<path-to-server-certificate>
pkey=<path-to-server-private-key>
min-port=<relay-range-first-port>
max-port=<relay-range-last-port>
```

Use use-auth-secret rather than configuring static user passwords. Arrange DNS,
trusted TLS certificates for turns:, clock synchronization, firewall rules for the
advertised listeners and relay port range, and external-ip mapping if coturn is
behind NAT. Keep its secret configuration access restricted. Never copy real
secrets into this example. Production relay allocation and client media must be
verified separately; automated tests here use no live coturn server.

References: [coturn TURN REST API](https://github.com/coturn/coturn/blob/master/README.turnserver)
and [coturn configuration example](https://github.com/coturn/coturn/blob/master/examples/etc/turnserver.conf).
