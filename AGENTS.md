# Aerochat Visual Shell

## Project status

This repository is a visual-only WPF shell derived from the archived `not-nullptr/Aerochat` project. It preserves the Windows Live Messenger 2009 visual language and assets, but does not connect to Discord or any other backend.

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

## Non-negotiable product boundary

This is a presentation shell, not a Discord client. Do not reintroduce:

- Discord/DSharpPlus or any network client
- authentication, tokens, MFA, OAuth, WebView login, or credential storage
- voice sockets, audio networking, or remote presence updates
- HTTP requests, WebSockets, upload/download behavior, or update checks
- settings persistence, registry/config writes, session files, or durable user state
- subprocesses, browser launches, external URL launches, installer/update launchers
- named-pipe IPC, single-instance protocol handling, tray integration, taskbar presence integration
- Win32/P/Invoke/DWM/native menu implementations
- backend-only generated protobuf, service, hoarder, or legacy ViewModel layers

All runtime state is process-local and resets when the application exits. Controls that used to have external behavior must either mutate local presentation state or be visibly inert. Never make an inert visual control silently perform an external side effect.

Preserve the existing WLM 2009 visual design. Do not modernize the layout, replace the art direction, flatten the titlebars, remove the scene system, or substitute generic controls when the existing visual control can be retained.

## Repository map

### Application

- `Aerochat/App.xaml`
  - Global WPF resource dictionaries, button/scrollbar themes, image rendering defaults, and shared styles.
- `Aerochat/App.xaml.cs`
  - Minimal startup path: create `DemoData`, create `WindowNavigator`, construct Home, assign `MainWindow`, and show it.
  - It still contains a few no-op compatibility shims for legacy windows. They are not active backend behavior and are intended to disappear as the remaining legacy surfaces are cleaned.
- `Aerochat/Aerochat.csproj`
  - WPF x64 executable project. Resources are included with wildcards from `Resources`, `Scenes`, `Ads`, and `Icons`.

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
  - Local visual settings, inert information/login UI, and process-local scene selection.
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
  - Confirms the solution contains only Aerochat and `Aerochat.VisualShell.Tests`.
  - Confirms the final product source contains no forbidden backend/runtime directories or tokens.
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

The final verified result was 87 passing tests, 0 failures, 0 skips, and a solution build with 0 warnings and 0 errors.

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
5. Open Settings, About, Login, ChangeScene, and ImagePreviewer. Login must not read or store credentials. Links must remain inert.
6. Confirm no `Aerochat.exe` socket appears in `netstat -ano`.
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
- The final solution includes only the active Aerochat and visual-shell test projects. A root-level `Aerotest.csproj` remains as an orphan file outside `Aerochat.sln`; it is not an active project and should not be re-added without an explicit decision.
- `docs/superpowers/specs/` and `docs/superpowers/plans/` contain the approved design and implementation plan. `.superpowers/sdd/` contains ignored execution reports and ledgers, not product runtime code.
- The inherited upstream `README.md` still describes the original Discord client and is not an accurate runtime contract for this shell.

## Do not do

- Do not add a Discord client or “temporary” fake backend abstraction.
- Do not add credentials, API keys, tokens, browser login, or persistence.
- Do not delete or replace WLM assets to make a binding easier.
- Do not use Any CPU for tests/builds.
- Do not weaken boundary/resource tests to make them pass.
- Do not claim visual/runtime success from compilation alone. Launch the actual executable for a visual change.
