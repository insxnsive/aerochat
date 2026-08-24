# Task 3 — visual shell control-boundary report

## Batch 3A evidence

Source: `deleg_6cf41375/task-0.log`, lines 77–88.

- Commit: `0ffe788` (`refactor: make shared controls presentation only`).
- App Debug x64 build: completed with **0 errors**.
- Presentation boundary: intentionally **RED** after Batch 3A; the remaining backend/native offenders were reserved for the later control batches.

## Batch 3B — WPF compatibility wrappers

### Scope

Modified only:

- `Aerochat/Controls/AttachmentsEditor/PopupBehavior.cs`
- `Aerochat/Controls/InteropContextMenu.cs`
- `Aerochat/Controls/NativeToolTip.cs`

### Implementation

- `PopupBehavior` now retains the `PopupContainer` attached dependency property and manages `Popup.IsOpen`, WPF focus, placement, window movement, activation, deactivation, and cleanup through WPF events only.
- `InteropContextMenu` now derives from standard WPF `ContextMenu`, retains the legacy dependency properties and menu-item model, rebuilds WPF `MenuItem` instances, and maps legacy placement values to WPF placement modes.
- `NativeToolTipControl` remains a `ToolTip` subclass with WPF `Text` and placement support, the legacy attached `ToolTip` property, WPF parent lookup, and a no-op `Destroy()` compatibility member.
- Removed WebView/native window/menu/hook implementations and all `SettingsManager`, `Vanara`, `DllImport`, `PInvoke`, `WebView2`, `Process`, `ShellExecute`, `Hwnd`, and native-hook references from these three files.

### Verification commands and output

```text
$ dotnet build Aerochat/Aerochat.csproj -c Debug -p:Platform=x64
    609 Warning(s)
    0 Error(s)
Time Elapsed: 00:00:07.15
(exit 0)
```

```text
$ dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter PresentationControlTests
Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5
(exit 1; expected boundary RED)
```

The focused boundary test reported exactly these remaining offenders:

```text
Aerochat\Controls\AudioPlayer.xaml.cs: Vanara
Aerochat\Controls\AudioPlayer.xaml.cs: PInvoke
Aerochat\Controls\AudioPlayer.xaml.cs: HttpClient
Aerochat\Controls\MessageParser.cs: DSharpPlus
Aerochat\Controls\MessageParser.cs: SettingsManager
Aerochat\Controls\MessageParser.cs: Vanara
Aerochat\Controls\ProfilePictureFrame.xaml.cs: DSharpPlus
Aerochat\Controls\ButtonTheme\ButtonBackgroundImage.cs: DSharpPlus
```

```text
$ git diff --check && git status --short
M Aerochat/Controls/AttachmentsEditor/PopupBehavior.cs
M Aerochat/Controls/InteropContextMenu.cs
M Aerochat/Controls/NativeToolTip.cs
(exit 0)
```

A direct forbidden-token scan of the three Batch 3B files returned `clean` for each file.

### Commit

- Batch 3B implementation commit: `f85b8c4` (`refactor: make popup compatibility controls WPF-only`).
- This report is committed separately so the implementation commit hash can be recorded exactly.

## Self-review and remaining work

- The three requested compatibility controls are presentation-only WPF wrappers and remain build-compatible with the app.
- No titlebar, profile-picture, media, text, window, or project files were edited in Batch 3B.
- The expected remaining boundary offenders are confined to `AudioPlayer.xaml.cs`, `MessageParser.cs`, `ProfilePictureFrame.xaml.cs`, and `ButtonTheme/ButtonBackgroundImage.cs`; no Batch 3B file appears in the focused-test offender list.
