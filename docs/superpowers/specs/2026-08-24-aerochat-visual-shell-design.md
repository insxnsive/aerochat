# Aerochat Visual-Only Shell Design

## Status

Approved in chat on 2026-08-24.

## Context

The upstream Aerochat repository is an archived WPF Discord client styled after Windows Live Messenger 2009. The local baseline builds successfully with 0 errors and 655 warnings. The main application currently contains 121 C# files, 27 XAML files, about 34,000 C# lines, about 5,000 XAML lines, 134 XAML event bindings, 13 NuGet package references, and direct references to the bundled DSharpPlus, Aerovoice, and Aerobool projects.

The visual layer is tightly coupled to Discord entities, networking, token storage, settings persistence, voice, WebView login, update checks, Win32 helpers, and process/file launching. The requested result is not a mocked Discord client. It is a reusable visual shell that preserves the Aerochat look while removing backend and operating-system integration logic.

## Design Read

Preserve the existing Windows Live Messenger 2009 visual language exactly. Do not redesign, modernize, restyle, replace assets, or introduce a new component system. Existing XAML, image assets, scenes, spacing, typography, colors, titlebars, control templates, hover states, and animations are the visual source of truth.

## Goals

1. Produce a buildable and launchable WPF visual shell.
2. Launch directly into a populated Home window using deterministic in-memory sample data.
3. Keep local presentation interactions that are required to inspect and reuse the UI:
   - open and close windows;
   - navigate from Home to Chat, Settings, About, Login, scene selection, image preview, and dialogs where the existing UI exposes those paths;
   - filter sample contacts and servers;
   - switch in-memory scenes and visual settings;
   - exercise menus, dropdowns, tabs, expand/collapse states, input fields, and visual-only send/edit states;
   - minimize, maximize, restore, drag, and close normal WPF windows.
4. Preserve reusable visual resources and custom controls.
5. Remove external communication, account behavior, durable state, and backend-shaped dependencies.
6. Leave a clear, small presentation boundary that future work can extend.

## Non-Goals

- Connecting to Discord or any replacement service.
- Authenticating, storing credentials, handling MFA, or opening an OAuth/WebView login.
- Sending messages, joining calls, updating presence remotely, downloading attachments, fetching notices, or checking for releases.
- Persisting settings, sessions, messages, or user data.
- Launching browsers, installers, media, files, subprocesses, tray applications, or external URLs.
- Preserving updater, crash-report transport, single-instance IPC, protocol handlers, or system-wide hooks.
- Preserving bundled backend libraries for possible future use.
- Redesigning or improving the WLM 2009 visuals.

## Recommended Architecture

### Solution

The solution will contain only:

- `Aerochat/Aerochat.csproj`: the WPF visual application.
- `Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj`: focused tests for the presentation boundary, deterministic sample state, resource integrity, and local navigation.

All other runtime, test, installer, and backend projects will be removed from the solution and repository.

### Presentation Boundary

Add a small `Aerochat/Presentation/` namespace with four responsibilities:

1. `DemoData`: constructs deterministic sample users, groups, servers, conversations, messages, notices, ads, themes, and settings.
2. `PresentationState`: owns mutable in-memory UI state for the current process only.
3. `WindowNavigator`: opens retained WPF windows and passes presentation models between them.
4. `PresentationModels`: simple UI-facing models with no Discord, network, protobuf, voice, persistence, or operating-system types.

These are presentation fixtures, not a fake service layer. They exist only to keep the visuals populated and locally inspectable.

### Startup

`App.xaml.cs` will become a minimal WPF application entry point:

1. initialize XAML resources;
2. construct the presentation state from `DemoData`;
3. open Home;
4. perform normal WPF shutdown when the last window closes.

Startup will not inspect tokens, command-line protocols, named pipes, updates, crash reports, settings files, encryption APIs, taskbar presence, tray state, foreground applications, or network readiness.

### Windows and Controls

Retain and adapt the visual surfaces that provide reusable UI:

- Home
- Chat
- Settings
- About
- Login as a presentation-only screen
- ChangeScene
- ImagePreviewer
- Notification
- Dialog
- ColorPicker
- NonNativeTooltip
- AerochatWindow and the custom titlebar/control system
- reusable controls under `Controls/`

Remove backend-only or support-only surfaces:

- DiscordLoginWV2
- WebView2Frame
- DebugWindow
- CrashReport
- MessageWindow IPC

Retained windows may keep concise code-behind for local UI behavior. Code-behind must not call network clients, persistence, subprocesses, external URLs, Discord models, voice services, or native P/Invoke. If an existing control requires Win32 code only to reproduce a visual effect, replace that implementation with standard WPF behavior while preserving its appearance.

### Models and Bindings

Existing view models may be retained when they are already presentation-only. Any model that exposes DSharpPlus, protobuf, voice, settings-manager, network, filesystem, or process types will be replaced or reduced to plain presentation properties.

Sample content will be deterministic so tests and screenshots remain stable. It will include:

- one current user with a scene, avatar, status, and personal message;
- favorites, conversations, and server categories;
- online, away, busy, and offline contacts;
- at least one populated direct chat and one populated group/server chat;
- representative incoming and outgoing messages, timestamps, reply styling, attachment visuals, typing state, and notification state where those components already exist;
- representative values for every retained Settings section;
- local ad, scene, locale, icon, emoji, and audio-control visuals from packaged resources.

Visual-only message entry may append an in-memory sample bubble or update a local draft. It must never perform I/O.

### Resources and Localization

Keep:

- `Resources/`
- `Scenes/`
- `Ads/` when used by the retained visual sample
- `Locales/`
- icons and application branding
- XAML resource dictionaries and WPF Aero theme resources

Packaged read-only resources are allowed. Runtime writes are not. Localization may load embedded or output-bundled locale files, but language selection must remain process-local and must not save a preference.

### Dependencies

Remove project references to:

- DSharpPlus
- Aerovoice
- Aerobool

Remove NuGet packages whose only purpose is removed logic, including:

- Google.Protobuf
- Lib.Harmony
- Markdig and MdXaml when no retained presentation control requires them
- Microsoft.Web.WebView2
- MimeTypeMapOfficial
- System.Speech
- Vanara P/Invoke packages
- Websocket.Client

Keep only packages proven necessary for the retained visual layer. `XamlAnimatedGif` is expected to remain because animated image rendering is a visual concern. `System.Drawing.Common` may remain only if a retained visual control cannot be converted cleanly to WPF-native image types.

### Removed Repository Areas

Delete after references are removed and tests identify no remaining dependency:

- `DSP/`
- `Aerovoice/`
- `Aerobool/`
- `Aerotest/`
- root `Aerotest.csproj`
- `Installer/`
- `Dynamic/`
- backend-only application folders and generated protocol code, including `Voice/`, `Services/`, `Hoarder/`, and `Protobuf/`
- login WebView assets and custom apphost material that no longer serve the visual app

The MPL-2.0 license and relevant attribution remain.

## Interaction Rules

- Local navigation and presentation state are allowed.
- Normal WPF visual behavior is allowed.
- All sample state resets when the process exits.
- Controls that previously caused external side effects either perform an equivalent in-memory visual state change or become visibly disabled when no meaningful local behavior exists.
- No button may silently retain an external side effect.
- Sign out opens the presentation-only Login window. Its continue action returns to Home without accepting, validating, or storing a credential.
- Links may remain visually styled but must not launch a browser.
- Attachment and image preview controls use packaged sample resources only.
- Audio controls may demonstrate play/pause/seek state without playing or loading media.

## Testing Strategy

Implementation follows test-driven development.

### Boundary Test

Write and run a failing test before removal work. It will assert that the product project and source contain none of the following:

- project references to bundled backend projects;
- package references for removed backend and OS-integration libraries;
- `DSharpPlus`, `Aerovoice`, protobuf, WebView2, websocket, HTTP client, cryptography/token, named-pipe, process-launch, settings-write, or P/Invoke dependencies;
- source folders designated for deletion.

The initial test must fail against the upstream baseline for the expected reasons. It becomes the primary measurable definition of "logic stripped."

### Demo Data Test

Write a failing test for the expected deterministic sample state, then add the minimal presentation models and `DemoData` required to pass it. Verify categories, representative contacts, chats, messages, scenes, and settings are populated with stable identifiers and values.

### Navigation and Construction Tests

On an STA test thread, construct each retained window with presentation state and verify that it loads without a backend service. Test local navigator routes and important in-memory state changes without opening network, file, process, or persistence paths.

### Resource Integrity Test

Verify that every packaged image, scene, locale, and XAML resource referenced by retained visual surfaces exists and is included correctly in the build.

### Build and Runtime Verification

1. Build Debug x64 with 0 errors.
2. Run the full test suite.
3. Launch the compiled application directly.
4. Inspect Home, Chat, Settings, About, Login, scene selection, and image preview.
5. Exercise local navigation, filtering, menus, scene switching, and visual-only state changes.
6. Verify the process performs no network connection and creates or modifies no user settings or token files.
7. Re-run the boundary test after runtime inspection.

The upstream warning count is baseline context, not an accepted final target. Warnings introduced by the visual-shell change must be fixed. Existing warnings disappear naturally as backend code is removed. Any remaining warnings in retained presentation code are reviewed rather than ignored.

## Migration Sequence

1. Record the clean upstream baseline build.
2. Add the failing visual-boundary test.
3. Reduce the solution and project dependency graph until the boundary test passes.
4. Add failing deterministic demo-data tests.
5. Implement presentation models and sample data.
6. Replace application startup with direct visual-shell startup.
7. Adapt Home, then Chat, then secondary windows in vertical slices. For each slice, add or update a construction/navigation test before production changes.
8. Remove dead backend folders and assets only after retained XAML no longer references them.
9. Run resource integrity, build, test, and live visual verification.
10. Perform one independent final review focused on accidental backend retention, visual regressions, and future reuse.

## Risks and Mitigations

### Visual logic is mixed with backend logic

Mitigation: keep concise code-behind only for presentation behavior and replace backend-fed values with plain models. Do not preserve a fake Discord abstraction merely to avoid editing bindings.

### Custom titlebars use native helpers

Mitigation: preserve the XAML and image treatment, then replace P/Invoke behavior with WPF `WindowChrome` and normal window commands.

### Deleting a backend type breaks many bindings

Mitigation: migrate one visual surface at a time with deterministic models and construction tests. Do not delete a shared model until its retained bindings have a presentation replacement.

### The shell builds but opens empty windows

Mitigation: deterministic demo-data assertions and live visual inspection are required acceptance gates.

### External behavior survives inside a click handler

Mitigation: boundary scans, targeted searches for I/O APIs, code review, and live observation of network and file activity.

### Visual drift during cleanup

Mitigation: no redesign changes are in scope. Preserve XAML and assets whenever possible. Compare retained windows against the upstream application structure during live verification.

## Acceptance Criteria

The task is complete only when all of the following are true:

- The repository at `C:/Users/insxn/Documents/chataero.v2` builds as a visual-only WPF solution.
- The app launches directly into a populated Home window without login or network access.
- Retained visual surfaces open and render with deterministic sample content.
- Local presentation interactions work without persistence or external side effects.
- The solution and product source contain no Discord, voice, updater, WebView login, token, network, persistence, subprocess, IPC, or P/Invoke logic.
- Bundled backend, installer, dynamic-content, and obsolete test projects are removed.
- Retained resources and WLM 2009 visual styling remain intact.
- Focused tests pass, the final Debug x64 build has 0 errors, and live visual inspection passes.
- No repository push is performed.
