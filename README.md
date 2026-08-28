# Aerochat

Aerochat is an early-stage Windows chat client with the visual language of Windows Live Messenger 2009.

This repository is where the project is being rebuilt and extended. It is not a release, a release candidate, or a finished product. The client is still changing quickly, the hosted service is not available yet, and parts of the application are unfinished. Expect rough edges, breaking changes, incomplete flows, and documentation that will keep moving with the code.

## What we are building

The long-term goal is a modern chat service with an old-school desktop client. The WLM-inspired shell is the part worth keeping. The old Discord-specific backend is not.

The repository now contains work toward:

- A Windows desktop client built with WPF and .NET 8.
- An offline-first visual shell with deterministic DemoData for development.
- A separate connectivity layer for authentication, realtime events, conversation data, and calls.
- An ASP.NET Core server with REST conversation history, a WebSocket gateway, OAuth login, call signaling, and a GIF proxy.
- One-to-one voice call support under active development.
- A self-hostable server for development and deployment experiments while the hosted product takes shape.

The original Aerochat project was an archived Discord client. This project started from that codebase, but the architecture and product direction are changing substantially. The old README and old Discord assumptions should not be treated as current documentation.

## Current status

**Development only. Not ready for release or normal daily use.**

There are no official downloads, supported deployments, or stability guarantees. The current branch is being used to build the visual shell, replace legacy service assumptions, and establish the client/server boundaries. Some controls are intentionally local or inert until their connectivity path is ready.

Do not use this repository as a production chat service. Do not put real user data, provider credentials, or deployment secrets into the repository or an unreviewed local server configuration.

## Building on Windows

Requirements:

- Windows with the .NET 8 SDK.
- Visual Studio 2022 with the .NET desktop development workload, or an equivalent .NET 8 build environment.
- An x64 build. The solution is not supported with Any CPU because of native-sensitive rendering dependencies.

From the repository root:

```bash
dotnet restore Aerochat.sln
dotnet test Aerochat.sln -c Debug -p:Platform=x64 --no-restore
dotnet build Aerochat.sln -c Debug -p:Platform=x64 --no-restore
```

The WPF executable is produced at:

```text
Aerochat/bin/x64/Debug/net8.0-windows7.0/Aerochat.exe
```

The test suite includes the WPF client, presentation and resource checks, connectivity behavior, server APIs, gateway behavior, OAuth flows, and loopback WebSocket integration tests.

## Running the client

With `AEROCHAT_SERVER_URL` unset, the application starts in offline DemoData mode. This is the default development path and does not require a server or network connection.

To point the client at a server, set an absolute HTTP or HTTPS origin:

```text
AEROCHAT_SERVER_URL=https://chat.example.com
```

Authentication, token caching, server configuration, and provider setup are documented in:

- [`docs/client-authentication.md`](docs/client-authentication.md)
- [`docs/authentication.md`](docs/authentication.md)
- [`docs/database.md`](docs/database.md)

Never commit provider secrets, access tokens, database credentials, or deployment configuration.

## Project layout

- `Aerochat/` contains the WPF client and its retained visual surfaces.
- `Aerochat/Presentation/` contains UI-facing state and local presentation models.
- `Aerochat/Controls/` contains the WLM-style visual control infrastructure. It must remain free of networking, persistence, process launch, and native integration.
- `Aerochat/Connectivity/` contains client networking, authentication, realtime transport, and RTC code.
- `Aerochat.Server/` contains the ASP.NET Core backend.
- `Aerochat.VisualShell.Tests/` contains client, presentation, resource, and runtime smoke tests.
- `Aerochat.Server.Tests/` contains server and loopback integration tests.
- `docs/` contains the current architecture and behavior notes.
- `AGENTS.md` contains the detailed development rules for this repository.

The main boundaries are deliberate. The app should still launch and remain useful as a visual shell when no server is configured. Live behavior must pass through `Aerochat.Connectivity`, and server behavior belongs in `Aerochat.Server`.

## Development notes

This project is still being shaped. Before changing a visual surface, preserve the existing WLM layout, scene system, titlebars, image assets, and nine-slice controls. Before changing behavior, check the relevant tests and the rules in `AGENTS.md`.

The project does not use telemetry, update checkers, analytics, crash uploaders, or unrelated background services. Any future network behavior needs to stay inside the connectivity or server layers.

## License and origin

Aerochat is licensed under the Mozilla Public License 2.0. See [`LICENSE`](LICENSE).

The project derives from the archived [`not-nullptr/Aerochat`](https://github.com/not-nullptr/Aerochat) repository. The original license and attribution remain in this repository. The current codebase is a separate development project with a different product direction and should not be confused with the archived Discord client.
