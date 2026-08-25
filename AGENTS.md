# Aerochat Visual Shell

## Project status

This repository contains a WPF client plus a self-hostable server component, derived from the archived `not-nullptr/Aerochat` project. It preserves the Windows Live Messenger 2009 visual language and assets while live features land behind explicit connectivity boundaries.

- Primary working branch: `visual-shell`
- Local project path: `C:/Users/insxn/Documents/chataero.v2`
- Solution: `Aerochat.sln`
- Runtime project: `Aerochat/Aerochat.csproj`
- Test project: `Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj`
- Runtime target: `.NET 8 WPF`, `net8.0-windows7.0`, x64 only
- Rendering package: `XamlAnimatedGif` 2.3.0
- The final verified solution build passed with 0 warnings and 0 errors.
- No remote push is part of this project workflow.

`README.md` is inherited upstream documentation and still describes the original Discord client. This file is the current development guide.

## Layering contract (supersedes the former no-networking boundary)

1. `Aerochat.Presentation` and `Aerochat/Controls` remain PURE: no I/O, no sockets,
   no process launch, no persistence, no P/Invoke. Boundary tests keep scanning these
   trees for forbidden tokens. Inert controls stay visibly inert unless wired through
   Aerochat.Connectivity.
2. `Aerochat.Connectivity` (new namespace) owns ALL client networking: transports,
   token cache, realtime protocol, RTC engine. Only this namespace may contain
   socket/HTTP/data-protection types.
3. `Aerochat.Server` (new project) is the self-hostable backend. Secrets and deploy
   config never enter git.
4. Offline-first: DemoData remains the default mode. With no server configured the
   app behaves exactly like the visual shell does today.
5. Still forbidden globally: telemetry, update checkers, analytics, crash uploaders,
   and any behavior not explicitly routed through layers 2 or 3.

Preserve the WLM 2009 visual design mandate: do not modernize the layout, replace
the art direction, flatten the titlebars, remove the scene system, or substitute
generic controls when the existing visual control can be retained.

## Repository map

### Application

- `Aerochat/App.xaml`
  - Global WPF resource dictionaries, button/scrollbar themes, image rendering defaults, and shared styles.
- `Aerochat/App.xaml.cs`
  - Composition root: create `DemoData`, choose configured or offline authentication, inject retained-window factories into `WindowNavigator`, construct Home, assign `MainWindow`, and show it.
  - It still contains a few no-op compatibility shims for legacy windows. They are not active backend behavior and are intended to disappear as the remaining legacy surfaces are cleaned.
- `Aerochat/Aerochat.csproj`
  - WPF x64 executable project. Resources are included with wildcards from `Resources`, `Scenes`, `Ads`, and `Icons`.

### Connectivity (Phase 1+)

- `Aerochat/Connectivity/` owns ALL client networking: transports, OAuth token
  cache, realtime gateway protocol, and the RTC media engine.
- Only this namespace may contain socket/HTTP/data-protection types. Presentation,
  Controls, and Windows code-behind reach connectivity exclusively through
  interfaces injected at the composition root (`App.xaml.cs`).
- `docs/client-authentication.md` is the desktop OAuth, loopback callback, offline
  behavior, timeout, and DPAPI-cache contract.

### Server (Phase 0+)

- `Aerochat.Server/` is an ASP.NET Core backend: REST history, WebSocket gateway,
  call signaling relay, GIF proxy. Joins `Aerochat.sln`; tests live beside it.
- Secrets, provider keys, and deployment configuration never enter the repository.

#### Server gateway (Task 8, implemented)

- `Gateway/GatewayHub.cs` — singleton connection registry and event fanout. Publishes
  assign monotonic sequences (`instanceId:sequence` cursors) under a lock, queue frames
  per sink, and drain outside the hot path. Replaces are dual-delivery: the old sink
  stays registered until the new registration fully succeeds, then the old is aborted
  with `Replaced`.
- `Gateway/GatewayConnection.cs` — bounded per-connection frame queue implementing
  `IGatewaySink`. `WaitForFrameAsync` blocks until a frame, `Complete`, or `Abort`;
  `TerminalAbortReason` exposes why the connection ended (`Closed`, `PolicyViolation`,
  `Overloaded`, ...).
- `Rest/GatewayEndpoints.cs` — `GET /ws?token=&lastEventId=`. Auth via exact query
  token through `ConversationAuth`; missing/invalid/no-local-user returns HTTP 401 +
  `WWW-Authenticate: Bearer`, authenticated non-upgrade GET returns 400. Push-only:
  inbound text/binary closes with policy violation (1008). Close codes: invalid/future
  cursor 1008, expired resync 1000 after ready+resync_required controls,
  server-restarted resync 1012, overload 1013, frame too large 1009, unexpected 1011.
- `Rest/ConversationMessageService.cs` — extracted send persistence + authz shared by
  REST. Persists exactly once; only after `SaveChanges` succeeds does it load
  participant user IDs server-side and publish one `message.created` to the hub.
  Audiences are never accepted from clients. Failed validation/authz/DB publishes
  nothing.
- `ServerComposition.cs` — shared composition root used by `Program.cs` and test
  fixtures hosting the real pipeline on loopback ports.
- Wire format: `{"t":"<event>","eventId":"<instance>:<seq>|null","d":{...}}`, camelCase
  payloads. Control events: `gateway.ready`, `gateway.resync_required`
  (`reason`: `cursor_too_old` | `server_restarted`).

### Presentation state

Everything new should depend on `Aerochat.Presentation`, not legacy service/model namespaces.

- `PresentationState.cs`
  - Owns current user, scenes, visual settings, contact groups, conversations, messages, ads, news, notices, previews, search results, and local state mutations.
- `DemoData.cs`
  - Deterministic fixture factory. It creates fresh independent graphs with fixed IDs, dates, text, colors, and packaged resource URIs.
- `WindowNavigator.cs`
  - Exhaustive local route factory for Home, Chat, Settings, About, Login, ChangeScene, and ImagePreviewer.
- `ShellRoute.cs`
  - Route enum used by the local navigator.
- `ObservableObject.cs`
  - `INotifyPropertyChanged` base for binding-facing state.
- `PersonPresentation.cs`, `PresenceStatus.cs`
  - User and presence models. Local statuses are `Online`, `Busy`, `Away`, and `Offline`.
- `ContactGroupPresentation.cs`
  - Contact/group models, observable selection/visibility/collapse state, and filtered/source-group synchronization.
- `ConversationPresentation.cs`, `MessagePresentation.cs`
  - Local conversation, draft, reply, edit, typing, attachment, and message state.
- `ScenePresentation.cs`, `VisualSettingsPresentation.cs`
  - Scene and process-local appearance settings.
- `ContentPresentation.cs`
  - News, notices, ads, preview images, `AdImageType`, and spritesheet metadata (`AnimationFrames`, `AnimationFramerate`).

### Windows

Retained visual surfaces live under `Aerochat/Windows`:

- `Home.xaml` and `Home.xaml.cs`
  - Main shell. The XAML keeps the WLM layout, assets, scenes, contact tree, search, notices, news, and ad presentation.
  - Code-behind is presentation-only. It handles local search, presence, personal-message editing, group collapse, notice dismissal, ad cycling, local navigation attempts, hover states, and safe no-ops.
- `Chat.xaml` and `Chat.xaml.cs`
  - Local conversation shell. The XAML preserves the original message/toolbar/composer visual system; the code-behind handles local draft, send, reply, edit, drawing, toolbar, and attachment-preview state.
- `Settings.xaml`, `About.xaml`, `Login.xaml`, `ChangeScene.xaml`
  - Local visual settings, information, provider-backed OAuth login UI, and process-local scene selection. Login keeps the WLM frame and reaches networking only through injected `IAuthClient`.
- `ImagePreviewer.xaml`, `Notification.xaml`, `Dialog.xaml`, `ColorPicker.xaml`, `NonNativeTooltip.xaml`, `AerochatWindow.xaml`
  - Local packaged-resource previews, notifications, dialogs, colors, tooltips, and generic visual host surfaces.

The route factory is the authoritative way to create retained windows. Do not instantiate legacy backend constructors from new code.

### Shared controls

`Aerochat/Controls` contains the WLM visual infrastructure:

- `BasicTitlebar.cs`, `NoDwmTitlebar.xaml(.cs)`
  - Standard WPF titlebar/window command behavior with `WindowChrome` and `SystemCommands`.
- `ProfilePictureFrame.xaml(.cs)`
  - Local presence frame mapping. It accepts legacy-looking bound values through backend-free normalization and loads WPF pack resources directly.
- `AudioPlayer.xaml(.cs)`
  - In-memory play/pause/seek/volume visuals only. It does not load or play audio.
- `MessageParser.cs`
  - Local text/BBCode/emoji rendering. Hyperlink events are presentation events; the parser never launches URLs. Unresolved channel mentions remain inert text.
- `AdImage.xaml(.cs)`
  - Preserves static, GIF, and spritesheet animation rendering. Demo data includes a real spritesheet fixture with frame metadata.
- `InteropContextMenu.cs`, `NativeToolTip.cs`, `AttachmentsEditor/PopupBehavior.cs`
  - Standard WPF compatibility wrappers. No native handles, WebView, or hooks.
- `NineSlice`, `ColorizedNineSlice`, `NineSliceButton`, `AnimatedTileImage`, profile and scrollbar controls
  - Preserve the original image-sliced visual treatment and interaction states.

The final controls boundary test scans every `Aerochat/Controls/**/*.cs` file for backend/native tokens. Keep it clean.

### Visual resources

- `Aerochat/Resources/`
  - Frames, emoji, home/message/titlebar/button/scrollbar assets, icons, sounds, and visual textures.
- `Aerochat/Scenes/`
  - Scene images and default scene assets.
- `Aerochat/Ads/`
  - Static/GIF/spritesheet ad assets and the inherited `Ads.xml` metadata.
- `Aerochat/Locales/`
  - Bundled `en.json` and `fr.json` locale data.
- `Aerochat/Icons/`
  - Application and visual icon assets.

Packaged resource paths must use the existing WPF conventions, for example:

```text
/Aerochat;component/Resources/Frames/PlaceholderPfp.png
pack://application:,,,/Aerochat;component/Resources/Frames/LargeFrameActiveAnimation.png
```

The full `pack://application:,,,/` form matters. A URI that merely ends in the right filename may still fail at runtime.

## Tests

Tests live in `Aerochat.VisualShell.Tests` and use NUnit with WPF targeting.

Important test files:

- `RepositoryLayoutTests.cs`
  - Confirms the solution contains only the approved client, server, and test projects.
  - Confirms the product source respects the Presentation/Controls purity boundary and keeps connectivity side effects in approved layers.
- `ResourceIntegrityTests.cs`
  - Checks XAML/code resource references and demo resources exist.
- `DemoDataTests.cs`
  - Deterministic fixture, local send/reply/edit/search/scene behavior, resource paths, and independent graphs.
- `PresentationControlTests.cs`
  - WPF frame URI/resource loading, legacy status normalization, context-menu behavior, local emoji/parser safety, and the shared controls boundary.
- `HomeShellTests.cs`
  - Home bindings, local actions, search, collapse/selection, locale validation, real executable startup smoke, and XAML/handler structure.
- `ChatShellTests.cs`
  - Chat construction, route lookup, local reply/send/edit state, and Chat XAML structure.
- `WindowNavigatorTests.cs`
  - Retained secondary-window route construction.
- `WpfTestHost.cs`
  - Durable STA dispatcher for unit-style WPF tests. It intentionally avoids creating an `Application` because doing so caused the NUnit process to hang. Real visual startup is tested separately by launching the executable.

Server-side test files:

- `Aerochat.Server.Tests/GatewayWebSocketIntegrationTests.cs`
  - Real-Kestrel loopback WebSockets (127.0.0.1:0): handshake auth (401/400),
    push-only policy close with hub cleanup, REST→gateway exactly-once persistence and
    participant-scoped fanout, reconnect replay filtering, expired-cursor resync
    (1000), previous-instance cursor resync (1012).
- `Aerochat.Server.Tests/GatewayHandshakeTests.cs`
  - TestServer-level handshake matrix: missing/blank/invalid token → 401 Bearer,
    valid local token without upgrade → 400.
- `Aerochat.Server.Tests/LoopbackServerFixture.cs`
  - Boots the real pipeline (Kestrel + WebSockets + isolated in-memory SQLite) via
    `ServerComposition` on an OS-chosen numeric loopback port.

## Exact verification commands

Run from `C:/Users/insxn/Documents/chataero.v2`.

Use x64 every time. Do not use the default Any CPU configuration because the project contains native-sensitive rendering dependencies and the solution explicitly maps x64.

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

The Task 5 verified baseline is 125 passing tests, 0 failures, 0 skips, and a solution build with 0 warnings and 0 errors.

## Launching and runtime checks

Compiled executable:

```text
Aerochat/bin/x64/Debug/net8.0-windows7.0/Aerochat.exe
```

For a manual smoke run:

1. Launch the executable.
2. Confirm Home shows the WLM titlebar, sample user, scene/header, search box, contact tree, news controls, and ad region.
3. Exercise local search, presence/status, personal message, group collapse, scene/settings changes, and local Chat navigation.
4. Open Chat and exercise local draft/send/reply/edit/attachment-preview controls.
5. Open Settings, About, Login, ChangeScene, and ImagePreviewer. Login must expose only Google/GitHub/Discord provider actions, never password/MFA/token inputs. Informational links remain inert.
6. With `AEROCHAT_SERVER_URL` unset, confirm Login reports `Server not configured`, provider actions are disabled, and no `Aerochat.exe` socket appears in `netstat -ano`.
7. Close/kill the tracked process and confirm no Aerochat process remains.

The final automated smoke launched the real executable, kept it alive long enough for inspection, found zero netstat rows for its PID, and left no orphan process.

## Development workflow

1. Read this file and inspect `git status` before editing.
2. Preserve the current visual system. Avoid unrelated cleanup while changing a visual surface.
3. Add or update a focused test before changing behavior. Observe the expected RED, then implement the smallest GREEN change.
4. Keep state in `Aerochat.Presentation`; do not resurrect legacy service/model dependencies.
5. Verify the affected focused tests, then the full x64 suite and solution build.
6. Use coherent commits with descriptive messages. Do not force-push, rewrite history, or publish.
7. If a visual change is needed, inspect the live executable or compare the relevant XAML/resource paths before replacing anything.

## Known caveats

- Scene color/state and local scene switching work. Scene imagery is deliberately not eagerly loaded in Home/secondary XAML because dynamic scene pack binding failed under the real STA resource host; do not reintroduce fragile eager loading without a real smoke test.
- The normal incremental build can expose old nullable/member-hiding warning noise in legacy retained controls/helpers. The final exact solution build was clean with 0 warnings and 0 errors. New warnings in changed presentation code are not acceptable.
- The solution includes the active client, server, and their test projects. A root-level `Aerotest.csproj` remains as an orphan file outside `Aerochat.sln`; it is not an active project and should not be re-added without an explicit decision.
- `docs/superpowers/specs/` and `docs/superpowers/plans/` contain the approved design and implementation plan. `.superpowers/sdd/` contains ignored execution reports and ledgers, not product runtime code.
- The inherited upstream `README.md` still describes the original Discord client and is not an accurate runtime contract for this shell.

## Do not do

- Do not add a Discord client or “temporary” fake backend abstraction.
- Do not add passwords, API keys, provider secrets, or arbitrary token inputs. Browser login and token persistence are permitted only through the documented OAuth/DPAPI implementation under `Aerochat.Connectivity`; secrets and tokens must never be logged or committed.
- Do not delete or replace WLM assets to make a binding easier.
- Do not use Any CPU for tests/builds.
- Do not weaken boundary/resource tests to make them pass.
- Do not claim visual/runtime success from compilation alone. Launch the actual executable for a visual change.
