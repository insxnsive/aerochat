# Contributing to Aerochat

Thank you for helping build Aerochat, a self-hostable chat platform and WPF client that preserves the Windows Live Messenger 2009 visual language. This repository is in active development: the client is offline-first, while networking and server features land behind explicit boundaries.

This guide is the contribution contract for the `visual-shell` branch. Read `AGENTS.md` before changing code; it is the current development guide. The inherited upstream `README.md` still describes the original Discord client and is not the runtime contract for this repository.

## Before you start

- Use Windows with the .NET 8 SDK installed. The client is WPF and targets `net8.0-windows7.0`.
- Build and test **x64 only**. Never select `Any CPU`; the solution explicitly maps x64 and includes native-sensitive rendering dependencies.
- A Visual Studio installation with the .NET Desktop Development workload is recommended for WPF/XAML work. The `dotnet` CLI commands below are authoritative.
- Keep provider secrets, API keys, signing keys, deployment configuration, databases, and session tokens out of the repository and out of logs.
- Do not switch away from the task's working branch or rewrite history. Do not force-push or publish from this workflow.

## Repository shape and design documents

- `Aerochat/` is the WPF client and visual shell.
- `Aerochat.VisualShell.Tests/` contains the client and presentation tests.
- `Aerochat.Server/` is the self-hostable ASP.NET Core backend.
- `Aerochat.Server.Tests/` exercises the real server pipeline, including loopback Kestrel/WebSocket behavior.
- Approved design and implementation material lives under `docs/superpowers/specs/` and `docs/superpowers/plans/`. Read the relevant documents before changing an established contract.

The WLM 2009 visual mandate is intentional. Do not modernize the layout, flatten the titlebars, remove the scene system, replace the art direction, or substitute generic controls when the existing visual control can be retained.

## Build and test

Run these commands from `C:/Users/insxn/Documents/chataero.v2`. Use x64 for every restore/build/test workflow.

```bash
# Restore if needed
dotnet restore Aerochat.sln

# Full solution test suite
dotnet test Aerochat.sln -c Debug -p:Platform=x64 --no-restore

# Full solution build
dotnet build Aerochat.sln -c Debug -p:Platform=x64 --no-restore

# Server smoke (after Phase 0 lands)
dotnet run --project Aerochat.Server             # starts API on http://localhost:5080
curl -s http://localhost:5080/health             # expect {"status":"ok"}

# Focused visual-shell project tests
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj \
  -c Debug -p:Platform=x64 --no-restore

# Focused test examples
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj \
  -c Debug -p:Platform=x64 --no-restore \
  --filter 'FullyQualifiedName~HomeShellTests'

dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj \
  -c Debug -p:Platform=x64 --no-restore \
  --filter 'FullyQualifiedName~ChatShellTests'

# Formatting and branch state
git diff --check
git status --short --branch
```

The checked baseline is a full x64 suite with 125 passing tests, 0 failures, 0 skips, and a solution build with 0 warnings and 0 errors. A change is not complete because it compiles: visual changes also need a real executable smoke check.

### Server smoke notes

`dotnet run --project Aerochat.Server` is a foreground process. Leave it running in one terminal and run the `curl` command from another terminal. The health response should be `{"status":"ok"}`. Use a separate development/test SQLite database rather than a production database or a personal deployment file.

## Test-driven development

Every behavior change should follow RED-GREEN:

1. Add or update the smallest focused test that describes the behavior.
2. Run it and observe the expected **RED** failure for the right reason.
3. Implement the smallest change that makes the test **GREEN**.
4. Refactor only after the behavior is covered and green.
5. Run the affected focused tests, then the full x64 solution suite and build.

Tests are part of the contract, not obstacles to work around. **Never weaken, delete, bypass, or broaden a boundary/resource test merely to make a change pass.** If a test exposes a real contract conflict, document the conflict and change the contract deliberately with the relevant design documentation and review.

For visual work, inspect the actual executable after the focused tests pass. Verify the retained WLM titlebar, scenes, controls, resource paths, and interaction states that the change touches. Compilation alone is not evidence of visual/runtime success.

## Layering contract

The current layering contract supersedes the former blanket no-networking boundary:

1. **Presentation and Controls are pure.** `Aerochat.Presentation` and `Aerochat/Controls` contain no I/O, sockets, process launch, persistence, or P/Invoke. Boundary tests scan these trees for forbidden tokens. Inert controls stay visibly inert unless wired through `Aerochat.Connectivity`.
2. **Connectivity owns all client networking.** `Aerochat.Connectivity` owns transports, OAuth token handling/cache, realtime protocol, and the RTC engine. Socket, HTTP, and data-protection types belong there. Windows and presentation code reach it through injected interfaces from the composition root.
3. **The server is separate.** `Aerochat.Server` is the self-hostable ASP.NET Core backend. Secrets and deployment configuration never enter git.
4. **Offline-first remains the default.** With no server configured, `DemoData` remains the default and the app behaves like the visual shell. No hidden network behavior should be added.
5. **Forbidden behavior remains forbidden globally.** Do not add telemetry, analytics, update checkers, crash uploaders, or other unrequested behavior outside the approved connectivity/server layers.

When adding a feature, decide first which layer owns it. Do not resurrect legacy service/model dependencies in new presentation code, and do not create a temporary fake backend abstraction in the client.

## Pull requests

A good pull request is small, reviewable, and proven:

- Keep the diff focused on one behavior or coherent design change. Avoid unrelated cleanup and formatting churn.
- Include focused tests with behavior changes. State the expected RED and the verified GREEN/full-suite results in the PR description.
- Run the exact x64 commands above; include any failure or environment limitation honestly.
- For visual changes, describe the affected WLM surface and include a screenshot or short manual verification note when useful. Respect the existing WLM visual system instead of introducing a modern replacement.
- Preserve resource paths, notices, and existing assets. Do not delete or replace an inherited WLM asset just to make a binding easier.
- Never commit API keys, OAuth client secrets, session signing keys, passwords, database files, or personal data.
- If a change affects a documented boundary, authentication flow, data contract, or release obligation, update the relevant documentation and point reviewers to the design/spec file.
- Finish with `git diff --check` and report the branch state. Do not commit or push on behalf of another workflow unless explicitly requested.

## Good first issues

- **Emoji shortcode table additions:** add a small, intentional alias to the emoji shortcode mapping, include a focused parser/resource test, and preserve the existing packaged visual asset conventions. Do not replace or remove WLM assets.
- **Documentation improvements:** clarify setup, OAuth configuration, server deployment, database backup, or the visual-shell test workflow without changing runtime behavior. Documentation-only fixes are welcome when they keep the current contracts accurate.

If you are unsure where a change belongs, start with `AGENTS.md`, the relevant file under `docs/superpowers/specs/`, and the existing focused tests. A narrowly scoped question in the issue or pull request is better than guessing at a boundary.
