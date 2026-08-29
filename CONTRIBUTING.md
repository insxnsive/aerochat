# Contributing to Aerochat

Aerochat is open to contributions, but it is still an unfinished development project. The client, server, protocols, and visual surfaces are changing together. Please do not treat the repository as a stable product or promise behavior that the code does not provide yet.

## Read this first

Read [`AGENTS.md`](AGENTS.md) before changing anything.

If you are an AI agent, or a person working alongside an AI agent, reading `AGENTS.md` is a fundamental part of working on this project. It explains the current project status, the architecture boundaries, the WLM visual design rules, the files that own networking, the things that must not be added, and the exact verification commands. Do not guess around it. Read it first and keep it in view while you work.

The repository is not ready for release. That is intentional context, not a missing disclaimer. Contributions can improve unfinished parts without pretending that the current build is production-ready.

## Before you start

1. Check the open issues and existing code before starting a duplicate change.
2. Read the relevant source files and nearby tests.
3. Keep changes focused. Avoid drive-by cleanup and unrelated reformatting.
4. If the change affects the visual shell, inspect the existing XAML and assets before replacing them.
5. Never add passwords, provider secrets, access tokens, or deployment credentials.

The default branch is `visual-shell`. Create a short-lived branch from it for your work:

```bash
git fetch origin
git switch visual-shell
git pull --ff-only origin visual-shell
git switch -c fix/short-description
```

Use branch names such as:

- `feat/short-description`
- `fix/short-description`
- `refactor/short-description`
- `docs/short-description`
- `test/short-description`
- `ci/short-description`

## Project boundaries

The visual direction matters here. Keep the Windows Live Messenger 2009 layout, titlebars, scenes, image assets, and nine-slice controls when working on the client. Do not replace them with generic modern controls just to make an implementation easier.

The code is split into deliberate boundaries:

- `Aerochat/Presentation/` contains UI-facing state and local presentation models.
- `Aerochat/Controls/` contains visual infrastructure and must remain free of networking, persistence, process launch, and native integration.
- `Aerochat/Connectivity/` owns client networking, authentication, realtime transport, and RTC code.
- `Aerochat.Server/` owns backend behavior.

Live client behavior must pass through `Aerochat.Connectivity`. The app must still launch in offline DemoData mode when no server is configured. Do not add telemetry, update checkers, analytics, crash uploaders, or unrelated background behavior.

## Build and test

Use x64 for every build and test command. Any CPU is not supported.

From the repository root:

```bash
dotnet restore Aerochat.sln
dotnet test Aerochat.sln -c Debug -p:Platform=x64 --no-restore
dotnet build Aerochat.sln -c Debug -p:Platform=x64 --no-restore
git diff --check
```

For a focused test run, use a project or test filter while keeping the x64 platform setting:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj \
  -c Debug -p:Platform=x64 --no-restore \
  --filter 'FullyQualifiedName~HomeShellTests'
```

Code changes should have tests where practical. A visual change is not verified by compilation alone. Launch the real executable and exercise the affected path when the change touches a visible surface or startup behavior.

## Commits and pull requests

Use concise Conventional Commit messages:

```text
feat: add conversation history loading
fix: keep the draft when switching chats
test: cover gateway cursor replay
docs: explain local server setup
```

A pull request should explain:

- what changed and why;
- which tests or checks were run;
- any known unfinished behavior;
- any visual or interaction changes, including the states you exercised.

Keep pull requests reviewable. If a change needs a larger design decision, open an issue or discussion before burying the decision in a large patch.

## Working with AI agents

AI-assisted contributions are welcome. They still need normal engineering review.

An AI agent must read `AGENTS.md` before editing. A person directing or reviewing an agent must read it too. The agent does not get to bypass the repository boundaries, skip required verification, invent APIs, or claim a test passed without running it. The human contributor remains responsible for the submitted change.

If an AI agent helped with a pull request, briefly state what it changed and what you personally verified. That makes review easier, especially while the project is still being rebuilt.

## License

Aerochat is licensed under the Mozilla Public License 2.0. Contributions are accepted under the terms of that license. Keep the existing license and attribution notices intact.
