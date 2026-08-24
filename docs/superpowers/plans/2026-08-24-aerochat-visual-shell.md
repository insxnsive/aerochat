# Aerochat Visual-Only Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the archived Aerochat Discord client into a launchable WPF visual shell populated by deterministic in-memory data, with no network, authentication, voice, persistence, updater, subprocess, IPC, or P/Invoke logic.

**Architecture:** Preserve the existing XAML, image resources, scenes, localization, control templates, and WLM 2009 styling. Replace backend-fed view models with plain presentation models owned by one process-local `PresentationState`, route retained windows through `WindowNavigator`, and prove the boundary with static repository tests plus STA window-construction tests.

**Tech Stack:** C# 12, .NET 8 WPF (`net8.0-windows7.0`), NUnit 3.14.0, NUnit3TestAdapter 4.5.0, Microsoft.NET.Test.Sdk 17.8.0, XamlAnimatedGif 2.3.0.

**Spec:** `docs/superpowers/specs/2026-08-24-aerochat-visual-shell-design.md`

## Global Constraints

- Work in `C:/Users/insxn/Documents/chataero.v2` on a local `visual-shell` branch.
- Do not push to `origin` or create a public repository.
- Preserve the existing Windows Live Messenger 2009 visual language. Do not redesign, modernize, restyle, or replace visual assets.
- Launch directly into a populated Home window.
- Keep local UI navigation, sample data, filtering, in-memory scene/settings changes, menus, dropdowns, tabs, expand/collapse states, drafts, and visual-only send/edit/reply states.
- Remove Discord, auth, token handling, voice, updates, persistence, WebView login, external URLs, file/process launching, tray/single-instance behavior, IPC, native hooks, and P/Invoke.
- Runtime state must reset when the process exits.
- Packaged read-only visual resources are allowed. Runtime writes are forbidden.
- The final solution contains only `Aerochat` and `Aerochat.VisualShell.Tests`.
- The final product project keeps only packages proven necessary for rendering. `XamlAnimatedGif` is expected to remain.
- Follow strict TDD for each behavior change: write the focused test, run it and observe the expected failure, implement the smallest passing change, run focused and full tests, then commit.
- Run Debug x64 builds. The original baseline is 0 errors and 655 warnings.
- Do not accept new warnings in retained presentation code.

## File Structure

### Create

- `Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj`: NUnit WPF test project.
- `Aerochat.VisualShell.Tests/RepositoryRoot.cs`: build-layout-independent repository root discovery.
- `Aerochat.VisualShell.Tests/RepositoryLayoutTests.cs`: solution and final backend-boundary assertions.
- `Aerochat.VisualShell.Tests/DemoDataTests.cs`: deterministic sample-state assertions.
- `Aerochat.VisualShell.Tests/PresentationControlTests.cs`: presentation-control boundary and frame-resource assertions.
- `Aerochat.VisualShell.Tests/HomeShellTests.cs`: Home construction, binding, search, and local status tests.
- `Aerochat.VisualShell.Tests/ChatShellTests.cs`: Chat construction and in-memory draft/edit/reply tests.
- `Aerochat.VisualShell.Tests/WindowNavigatorTests.cs`: retained route construction tests.
- `Aerochat.VisualShell.Tests/ResourceIntegrityTests.cs`: XAML and pack-resource integrity assertions.
- `Aerochat.VisualShell.Tests/WpfTestHost.cs`: single-STA test helper.
- `Aerochat/Presentation/ObservableObject.cs`: `INotifyPropertyChanged` base.
- `Aerochat/Presentation/PresenceStatus.cs`: local presence enum.
- `Aerochat/Presentation/PersonPresentation.cs`: current-user/contact presentation model.
- `Aerochat/Presentation/ContactGroupPresentation.cs`: Home category and contact model.
- `Aerochat/Presentation/MessagePresentation.cs`: local message and reply model.
- `Aerochat/Presentation/ConversationPresentation.cs`: direct/group conversation state.
- `Aerochat/Presentation/ScenePresentation.cs`: scene colors and image resource.
- `Aerochat/Presentation/VisualSettingsPresentation.cs`: process-local settings values.
- `Aerochat/Presentation/ContentPresentation.cs`: news, notice, ad, and preview-image records.
- `Aerochat/Presentation/PresentationState.cs`: mutable process-local state and presentation actions.
- `Aerochat/Presentation/DemoData.cs`: deterministic data factory.
- `Aerochat/Presentation/ShellRoute.cs`: retained navigation route enum.
- `Aerochat/Presentation/WindowNavigator.cs`: retained WPF window factory and show helper.

### Modify

- `Aerochat.sln`: keep only the visual app and new test project.
- `Aerochat/Aerochat.csproj`: remove backend references/packages, remove obsolete resources, keep WPF visual resources.
- `Aerochat/App.xaml`: retain resource dictionaries, remove bindings to deleted settings/native services.
- `Aerochat/App.xaml.cs`: minimal direct startup into Home.
- `Aerochat/AssemblyInfo.cs`: expose internals to the visual-shell tests if needed.
- `Aerochat/Windows/Home.xaml` and `.xaml.cs`: bind to `PresentationState` and perform only local UI actions.
- `Aerochat/Windows/Chat.xaml` and `.xaml.cs`: bind to `ConversationPresentation` and perform only local UI actions.
- `Aerochat/Windows/Settings.xaml` and `.xaml.cs`: edit `VisualSettingsPresentation` in memory.
- `Aerochat/Windows/About.xaml` and `.xaml.cs`: retain visuals; make links inert.
- `Aerochat/Windows/Login.xaml` and `.xaml.cs`: presentation-only return to Home, no credential handling.
- `Aerochat/Windows/ChangeScene.xaml` and `.xaml.cs`: select from `PresentationState.Scenes`.
- `Aerochat/Windows/ImagePreviewer.xaml` and `.xaml.cs`: packaged preview images only.
- `Aerochat/Windows/Notification.xaml` and `.xaml.cs`: presentation notification only.
- `Aerochat/Windows/Dialog.xaml` and `.xaml.cs`: WPF-native icon presentation.
- `Aerochat/Windows/ColorPicker.xaml` and `.xaml.cs`: retain local color selection.
- `Aerochat/Windows/NonNativeTooltip.xaml` and `.xaml.cs`: retain WPF tooltip presentation.
- `Aerochat/Windows/AerochatWindow.xaml` and `.xaml.cs`: retain generic visual host.
- `Aerochat/Controls/BasicTitlebar.cs`: WPF `WindowChrome` implementation only.
- `Aerochat/Controls/NoDwmTitlebar.xaml` and `.xaml.cs`: WPF window commands only.
- `Aerochat/Controls/ProfilePictureFrame.xaml.cs`: use local `PresenceStatus` and direct pack URIs.
- `Aerochat/Controls/AudioPlayer.xaml.cs`: in-memory play/pause/seek visuals only.
- `Aerochat/Controls/MessageParser.cs`: presentation text only, no Discord or URL launching.
- `Aerochat/Controls/ButtonTheme/ButtonBackgroundImage.cs`: local visual enum only.
- `Aerochat/Controls/AttachmentsEditor/PopupBehavior.cs`: pure WPF popup behavior.
- `Aerochat/Controls/InteropContextMenu.cs`: compatibility wrapper over WPF `ContextMenu` with no native calls.
- `Aerochat/Controls/NativeToolTip.cs`: compatibility wrapper over WPF `ToolTip` with no native calls.
- `Aerochat/Controls/TitlebarThemeManager.cs`: process-local titlebar state only.
- `Aerochat/Localization/LocalizationManager.cs`: packaged read-only locale loading, no settings lookup or restart.
- XAML files under `Aerochat/Controls/AttachmentsEditor/`: keep visual templates and bind to presentation attachment data only.

### Delete in the final purge

- `DSP/`
- `Aerovoice/`
- `Aerobool/`
- `Aerotest/`
- `Aerotest.csproj`
- `Installer/`
- `Dynamic/`
- `Aerochat/Voice/`
- `Aerochat/Services/`
- `Aerochat/Hoarder/`
- `Aerochat/Protobuf/`
- `Aerochat/Settings/`
- `Aerochat/Theme/`
- `Aerochat/ViewModels/`
- `Aerochat/WebDir/`
- `Aerochat/AppHostBin/`
- `Aerochat/Properties/Settings.Designer.cs`
- `Aerochat/Properties/Settings.settings`
- `Aerochat/Helpers/DiscordUserSettingsManager.cs`
- `Aerochat/Helpers/FixMicrosoftBadCodeMakingShitCrash.cs`
- `Aerochat/Helpers/OpenChatQueue.cs`
- `Aerochat/Helpers/SoundHelper.cs`
- `Aerochat/Helpers/StatusStringToUserStatusConverter.cs`
- `Aerochat/Helpers/TextToSpeech.cs`
- `Aerochat/Windows/DiscordLoginWV2.xaml` and `.xaml.cs`
- `Aerochat/Windows/WebView2Frame.xaml` and `.xaml.cs`
- `Aerochat/Windows/DebugWindow.xaml` and `.xaml.cs`
- `Aerochat/Windows/CrashReport.xaml` and `.xaml.cs`
- `Aerochat/Windows/MessageWindow.cs`

---

### Task 1: Establish the visual solution contract

**Files:**
- Create: `Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj`
- Create: `Aerochat.VisualShell.Tests/RepositoryRoot.cs`
- Create: `Aerochat.VisualShell.Tests/RepositoryLayoutTests.cs`
- Modify: `Aerochat.sln`

**Interfaces:**
- Consumes: repository root resolved from `TestContext.CurrentContext.TestDirectory`.
- Produces: an NUnit test project and a passing `Solution_contains_only_visual_app_and_tests` contract used by every later task.

- [ ] **Step 0: Create the local implementation branch**

```bash
git switch -c visual-shell
```

Expected: branch `visual-shell` starts from the committed design document on `main`.

- [ ] **Step 1: Create the test project file**

Use this exact project shape:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows7.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="NUnit" Version="3.14.0" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.5.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Aerochat\Aerochat.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="NUnit.Framework" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing solution-membership test**

```csharp
using System.Text.RegularExpressions;

namespace Aerochat.VisualShell.Tests;

internal static class RepositoryRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        for (DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
             directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Aerochat.sln")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not find Aerochat.sln above the test output directory.");
    }
}

public sealed class RepositoryLayoutTests
{
    private static string Root => RepositoryRoot.Path;

    [Test]
    public void Solution_contains_only_visual_app_and_tests()
    {
        string solution = File.ReadAllText(Path.Combine(Root, "Aerochat.sln"));
        string[] names = Regex.Matches(solution, "Project\\(.*?\\) = \\\"([^\\\"]+)\\\"")
            .Select(match => match.Groups[1].Value)
            .Where(name => name != "Solution Items")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.That(names, Is.EqualTo(new[] { "Aerochat", "Aerochat.VisualShell.Tests" }));
    }
}
```

- [ ] **Step 3: Run the focused test and observe RED**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter Solution_contains_only_visual_app_and_tests
```

Expected: FAIL because the solution still contains Aerobool, Aerotest, Aerovoice, and DSharpPlus.

- [ ] **Step 4: Reduce the solution membership**

Run:

```bash
dotnet sln Aerochat.sln remove Aerobool/Aerobool/Aerobool.csproj Aerotest/Aerotest.csproj Aerovoice/Aerovoice.csproj DSP/DSharpPlus/DSharpPlus.csproj
dotnet sln Aerochat.sln add Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj
```

Do not delete projects yet. This task changes solution membership only, leaving the baseline app buildable while migration proceeds.

- [ ] **Step 5: Verify GREEN and the baseline app build**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter Solution_contains_only_visual_app_and_tests
dotnet build Aerochat/Aerochat.csproj -c Debug -p:Platform=x64
```

Expected: the focused test passes; the app build remains at 0 errors.

- [ ] **Step 6: Commit the contract**

```bash
git add Aerochat.sln Aerochat.VisualShell.Tests
git commit -m "test: define visual shell solution boundary"
```

### Task 2: Add deterministic presentation state and demo data

**Files:**
- Create: `Aerochat/Presentation/ObservableObject.cs`
- Create: `Aerochat/Presentation/PresenceStatus.cs`
- Create: `Aerochat/Presentation/PersonPresentation.cs`
- Create: `Aerochat/Presentation/ContactGroupPresentation.cs`
- Create: `Aerochat/Presentation/MessagePresentation.cs`
- Create: `Aerochat/Presentation/ConversationPresentation.cs`
- Create: `Aerochat/Presentation/ScenePresentation.cs`
- Create: `Aerochat/Presentation/VisualSettingsPresentation.cs`
- Create: `Aerochat/Presentation/ContentPresentation.cs`
- Create: `Aerochat/Presentation/PresentationState.cs`
- Create: `Aerochat/Presentation/DemoData.cs`
- Create: `Aerochat.VisualShell.Tests/DemoDataTests.cs`

**Interfaces:**
- Consumes: WPF `Color`, `ObservableCollection<T>`, packaged resource URIs.
- Produces: `DemoData.Create(): PresentationState`; `PresentationState.ApplySearch(string)`; `PresentationState.SendDraft(ConversationPresentation, DateTimeOffset)`; `PresentationState.BeginReply(ConversationPresentation, MessagePresentation)`; `PresentationState.BeginEdit(ConversationPresentation, MessagePresentation)`; `PresentationState.CommitEdit(ConversationPresentation)`; `PresentationState.SelectScene(ScenePresentation)`.

- [ ] **Step 1: Write the failing deterministic-data test**

```csharp
using Aerochat.Presentation;

namespace Aerochat.VisualShell.Tests;

public sealed class DemoDataTests
{
    [Test]
    public void Create_populates_stable_visual_states()
    {
        PresentationState state = DemoData.Create();

        Assert.Multiple(() =>
        {
            Assert.That(state.CurrentUser.Name, Is.EqualTo("Nate Rivera"));
            Assert.That(state.ContactGroups.Select(group => group.Name),
                Is.EqualTo(new[] { "Favorites", "Conversations", "Servers" }));
            Assert.That(state.ContactGroups.SelectMany(group => group.Items)
                .Select(item => item.Person.Presence.Status).Distinct(),
                Is.SupersetOf(new[] { PresenceStatus.Online, PresenceStatus.Busy,
                    PresenceStatus.Away, PresenceStatus.Offline }));
            Assert.That(state.Conversations.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(state.Conversations.Any(conversation => conversation.IsGroup), Is.True);
            Assert.That(state.Conversations.Any(conversation => !conversation.IsGroup), Is.True);
            Assert.That(state.Conversations.All(conversation => conversation.Messages.Count >= 3), Is.True);
            Assert.That(state.Scenes.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(state.News.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(state.Notices, Is.Not.Empty);
        });
    }

    [Test]
    public void SendDraft_appends_one_local_outgoing_message_and_clears_the_draft()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        int before = conversation.Messages.Count;
        conversation.Draft = "Local shell message";

        MessagePresentation? sent = state.SendDraft(
            conversation, new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.Not.Null);
            Assert.That(conversation.Messages.Count, Is.EqualTo(before + 1));
            Assert.That(conversation.Messages[^1].Body, Is.EqualTo("Local shell message"));
            Assert.That(conversation.Messages[^1].IsOutgoing, Is.True);
            Assert.That(conversation.Draft, Is.Empty);
        });
    }
}
```

- [ ] **Step 2: Run the focused tests and observe RED**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter DemoDataTests
```

Expected: compilation fails because `Aerochat.Presentation` does not exist.

- [ ] **Step 3: Add the observable base and local enum**

Use this base contract:

```csharp
namespace Aerochat.Presentation;

public abstract class ObservableObject : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this,
            new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Notify([System.Runtime.CompilerServices.CallerMemberName]
        string? propertyName = null) => PropertyChanged?.Invoke(this,
            new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}
```

```csharp
namespace Aerochat.Presentation;

public enum PresenceStatus { Online, Busy, Away, Offline }
public enum MessageTargetMode { None, Reply, Edit }
public enum DrawingTool { Pen, Eraser }
```

- [ ] **Step 4: Add the presentation model contracts**

Use these exact public shapes so later XAML and tests agree:

```csharp
public sealed class PresencePresentation : ObservableObject
{
    private PresenceStatus _status;
    private string _activity = "";
    private string _customStatus = "";
    public PresenceStatus Status { get => _status; set => SetProperty(ref _status, value); }
    public string Activity { get => _activity; set => SetProperty(ref _activity, value); }
    public string CustomStatus { get => _customStatus; set => SetProperty(ref _customStatus, value); }
}

public sealed class PersonPresentation : ObservableObject
{
    public required ulong Id { get; init; }
    public required string Name { get; init; }
    public required string Username { get; init; }
    public required string Avatar { get; init; }
    public required PresencePresentation Presence { get; init; }
}

public sealed class ContactPresentation
{
    public required ulong ConversationId { get; init; }
    public required PersonPresentation Person { get; init; }
    public bool IsServer { get; init; }
}

public sealed class ContactGroupPresentation : ObservableObject
{
    private bool _isCollapsed;
    public required string Name { get; init; }
    public ObservableCollection<ContactPresentation> Items { get; } = [];
    public bool IsCollapsed { get => _isCollapsed; set => SetProperty(ref _isCollapsed, value); }
}

public sealed class MessagePresentation : ObservableObject
{
    private string _body = "";
    public required Guid Id { get; init; }
    public required PersonPresentation Author { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required bool IsOutgoing { get; init; }
    public string Body { get => _body; set => SetProperty(ref _body, value); }
    public string? AttachmentUri { get; init; }
    public MessagePresentation? ReplyTo { get; init; }
}

public sealed class ConversationPresentation : ObservableObject
{
    private string _draft = "";
    private string _typingText = "";
    private MessagePresentation? _targetMessage;
    private MessageTargetMode _targetMode;
    public required ulong Id { get; init; }
    public required string Name { get; init; }
    public required string Topic { get; init; }
    public required bool IsGroup { get; init; }
    public PersonPresentation? Recipient { get; init; }
    public ObservableCollection<PersonPresentation> Participants { get; } = [];
    public ObservableCollection<MessagePresentation> Messages { get; } = [];
    public string Draft { get => _draft; set => SetProperty(ref _draft, value); }
    public string TypingText { get => _typingText; set => SetProperty(ref _typingText, value); }
    public MessagePresentation? TargetMessage { get => _targetMessage; set => SetProperty(ref _targetMessage, value); }
    public MessageTargetMode TargetMode { get => _targetMode; set => SetProperty(ref _targetMode, value); }
}
```

`ScenePresentation` must expose `Id`, `DisplayName`, `File`, `Color`, `TextColor`, `ShadowColor`, and `IsDefault`. `VisualSettingsPresentation` must expose mutable `ShowAds`, `ShowNews`, `ShowEyecandy`, `ShowTimestamps`, `EnableAnimations`, `Language`, and `TimeFormat`. `ContentPresentation.cs` must define immutable `NewsPresentation`, `NoticePresentation`, `AdPresentation`, and `PreviewImagePresentation` records with only strings, dates, colors, and packaged resource URIs.

- [ ] **Step 5: Implement `PresentationState` actions**

Implement the public methods with only collection/property mutations. `SendDraft` trims the draft, returns `null` for whitespace, appends one outgoing message authored by `CurrentUser`, carries the current reply target when `TargetMode == Reply`, then clears draft and target state. `CommitEdit` updates only the selected local message body. `ApplySearch` creates filtered group wrappers without mutating the canonical groups. `SelectScene` sets `CurrentScene` and raises property notifications.

- [ ] **Step 6: Implement deterministic `DemoData.Create()`**

Use fixed IDs, fixed `DateTimeOffset` values, packaged avatar/frame/scene URIs, and stable visible strings. Populate exactly the three Home categories asserted by the test. Include at least one direct conversation and one group conversation, with incoming, outgoing, reply, attachment, and typing-state examples. Never call `DateTime.Now`, `Random`, a network client, a settings manager, or a filesystem API.

- [ ] **Step 7: Verify focused and full tests**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter DemoDataTests
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass.

- [ ] **Step 8: Commit the presentation state**

```bash
git add Aerochat/Presentation Aerochat.VisualShell.Tests/DemoDataTests.cs
git commit -m "feat: add deterministic visual shell state"
```

### Task 3: Convert shared controls to presentation-only WPF

**Files:**
- Create: `Aerochat.VisualShell.Tests/PresentationControlTests.cs`
- Modify: `Aerochat/Controls/BasicTitlebar.cs`
- Modify: `Aerochat/Controls/NoDwmTitlebar.xaml.cs`
- Modify: `Aerochat/Controls/ProfilePictureFrame.xaml.cs`
- Modify: `Aerochat/Controls/AudioPlayer.xaml.cs`
- Modify: `Aerochat/Controls/MessageParser.cs`
- Modify: `Aerochat/Controls/ButtonTheme/ButtonBackgroundImage.cs`
- Modify: `Aerochat/Controls/AttachmentsEditor/PopupBehavior.cs`
- Modify: `Aerochat/Controls/InteropContextMenu.cs`
- Modify: `Aerochat/Controls/NativeToolTip.cs`
- Modify: `Aerochat/Controls/TitlebarThemeManager.cs`
- Modify: `Aerochat/AssemblyInfo.cs`

**Interfaces:**
- Consumes: `PresenceStatus`, standard WPF `Window`, `WindowChrome`, `SystemCommands`, packaged resource URIs.
- Produces: `ProfilePictureFrame.GetFrameUri(PresenceStatus, ProfileFrameSize): Uri`; `AudioPlayer.IsPlaying`, `Position`, `Volume`, `TogglePlayback()`; native-free compatibility types for existing XAML.

- [ ] **Step 1: Write failing control-boundary tests**

```csharp
using Aerochat.Controls;
using Aerochat.Presentation;

namespace Aerochat.VisualShell.Tests;

public sealed class PresentationControlTests
{
    [TestCase(PresenceStatus.Online, ProfileFrameSize.Large, "LargeFrameActiveAnimation.png")]
    [TestCase(PresenceStatus.Busy, ProfileFrameSize.Small, "SmallFrameDndAnimation.png")]
    [TestCase(PresenceStatus.Away, ProfileFrameSize.ExtraSmall, "XSFrameIdle.png")]
    [TestCase(PresenceStatus.Offline, ProfileFrameSize.ExtraLarge, "XLFrameOffline.png")]
    public void Profile_frame_maps_local_presence_to_pack_resource(
        PresenceStatus status, ProfileFrameSize size, string expectedFile)
    {
        Assert.That(ProfilePictureFrame.GetFrameUri(status, size).AbsoluteUri,
            Does.EndWith(expectedFile));
    }

    [Test]
    public void Shared_controls_do_not_reference_backend_or_native_packages()
    {
        string root = RepositoryRoot.Path;
        string controls = Path.Combine(root, "Aerochat", "Controls");
        string[] forbidden = ["DSharpPlus", "Aerovoice", "SettingsManager",
            "Vanara", "DllImport", "PInvoke", "WebView2", "HttpClient",
            "Process.Start", "ShellExecute"];

        var offenders = Directory.EnumerateFiles(controls, "*.cs", SearchOption.AllDirectories)
            .Select(path => new { path, text = File.ReadAllText(path) })
            .SelectMany(file => forbidden.Where(file.text.Contains)
                .Select(token => $"{Path.GetRelativePath(root, file.path)}: {token}"))
            .ToArray();

        Assert.That(offenders, Is.Empty, string.Join(Environment.NewLine, offenders));
    }
}
```

- [ ] **Step 2: Run the tests and observe RED**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter PresentationControlTests
```

Expected: compile failure for `GetFrameUri` and boundary failures across current controls.

- [ ] **Step 3: Rewrite `ProfilePictureFrame` around `PresenceStatus`**

Change the dependency property type from `DSharpPlus.Entities.UserStatus` to `Aerochat.Presentation.PresenceStatus`. Load the frame directly from the pack URI instead of `Discord.ProfileFrames`:

```csharp
public static Uri GetFrameUri(PresenceStatus status, ProfileFrameSize size)
{
    string sizeName = size switch
    {
        ProfileFrameSize.ExtraSmall => "XS",
        ProfileFrameSize.ExtraLarge => "XL",
        ProfileFrameSize.Small => "Small",
        ProfileFrameSize.Medium => "Medium",
        ProfileFrameSize.Large => "Large",
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };
    string statusName = status switch
    {
        PresenceStatus.Online => "Active",
        PresenceStatus.Busy => "Dnd",
        PresenceStatus.Away => "Idle",
        PresenceStatus.Offline => "Offline",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
    string animation = statusName == "Offline" || sizeName == "XS" ? "" : "Animation";
    return new Uri($"pack://application:,,,/Aerochat;component/Resources/Frames/{sizeName}Frame{statusName}{animation}.png");
}
```

Preserve the existing transition animation and frame sizing.

- [ ] **Step 4: Replace native titlebar behavior with standard WPF behavior**

Keep the `BaseTitlebar` dependency properties and visual composition, but remove DWM detection, hooks, handles, non-client messages, and settings subscriptions. Apply one WPF `WindowChrome` instance on initialization:

```csharp
WindowChrome.SetWindowChrome(window, new WindowChrome
{
    CaptionHeight = 28,
    CornerRadius = new CornerRadius(6, 6, 0, 0),
    GlassFrameThickness = new Thickness(0),
    ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
    UseAeroCaptionButtons = false
});
```

`NoDwmTitlebar.xaml.cs` must use `SystemCommands.MinimizeWindow`, `MaximizeWindow`, `RestoreWindow`, and `CloseWindow`. Double-clicking the caption toggles maximize/restore. Dragging the caption calls `window.DragMove()` only on a left-button press.

- [ ] **Step 5: Replace native compatibility controls with WPF wrappers**

Keep class names used by XAML. `InteropContextMenu` becomes an empty subclass of `ContextMenu`. `NativeToolTipControl` becomes a WPF `ToolTip`-based control with text and placement dependency properties. `PopupBehavior` uses only dependency properties, `Popup.IsOpen`, and WPF focus events. No handle, hook, WebView, or native menu code remains.

- [ ] **Step 6: Make media and text controls visual-only**

`AudioPlayer` maintains only `IsPlaying`, `Position`, `Duration`, and `Volume`. Button handlers mutate those properties and update existing images. It must not load, download, decode, open, or play media. `MessageParser` may render plain text, local BBCode, timestamps, reply text, and packaged image references, but hyperlink clicks must only raise a presentation event and never launch anything. `ButtonBackgroundImage` uses a local visual-state enum. `TitlebarThemeManager` reads process-local values from the bound presentation state or dependency properties only.

- [ ] **Step 7: Verify the focused test, full suite, and app build**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter PresentationControlTests
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64
dotnet build Aerochat/Aerochat.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass and the app builds with 0 errors.

- [ ] **Step 8: Commit the control boundary**

```bash
git add Aerochat/Controls Aerochat/AssemblyInfo.cs Aerochat.VisualShell.Tests/PresentationControlTests.cs
git commit -m "refactor: make shared controls presentation only"
```

### Task 4: Launch directly into the populated Home shell

**Files:**
- Create: `Aerochat/Presentation/ShellRoute.cs`
- Create: `Aerochat/Presentation/WindowNavigator.cs`
- Create: `Aerochat.VisualShell.Tests/WpfTestHost.cs`
- Create: `Aerochat.VisualShell.Tests/HomeShellTests.cs`
- Modify: `Aerochat/App.xaml.cs`
- Modify: `Aerochat/Windows/Home.xaml`
- Modify: `Aerochat/Windows/Home.xaml.cs`
- Modify: `Aerochat/Localization/LocalizationManager.cs`

**Interfaces:**
- Consumes: `DemoData.Create()`, `PresentationState`, retained WPF windows.
- Produces: `ShellRoute` values `Home`, `Chat`, `Settings`, `About`, `Login`, `ChangeScene`, `ImagePreviewer`; `WindowNavigator.Create(ShellRoute, object?): Window`; `WindowNavigator.Show(ShellRoute, Window?, object?)`; `Home(PresentationState, WindowNavigator)`.

- [ ] **Step 1: Add one reusable STA test host**

```csharp
namespace Aerochat.VisualShell.Tests;

internal static class WpfTestHost
{
    private static readonly System.Windows.Threading.Dispatcher Dispatcher = StartDispatcher();

    private static System.Windows.Threading.Dispatcher StartDispatcher()
    {
        System.Windows.Threading.Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new Aerochat.App();
            app.InitializeComponent();
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true, Name = "Aerochat WPF test host" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    public static void Run(Action action)
    {
        Exception? failure = null;
        Dispatcher.Invoke(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        if (failure is not null) throw new AssertionException(failure.ToString());
    }
}
```

- [ ] **Step 2: Write failing Home construction and local-state tests**

```csharp
using Aerochat.Presentation;
using Aerochat.Windows;

namespace Aerochat.VisualShell.Tests;

public sealed class HomeShellTests
{
    [Test]
    public void Home_constructs_from_demo_state_without_backend_services()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var navigator = new WindowNavigator(state);
            var home = new Home(state, navigator);
            Assert.That(home.DataContext, Is.SameAs(state));
            Assert.That(state.FilteredContactGroups, Is.Not.Empty);
            home.Close();
        });
    }

    [Test]
    public void Search_filters_contacts_without_mutating_source_groups()
    {
        PresentationState state = DemoData.Create();
        int sourceCount = state.ContactGroups.Sum(group => group.Items.Count);
        state.ApplySearch("Mara");
        Assert.That(state.FilteredContactGroups.SelectMany(group => group.Items)
            .All(item => item.Person.Name.Contains("Mara", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(state.ContactGroups.Sum(group => group.Items.Count), Is.EqualTo(sourceCount));
    }
}
```

- [ ] **Step 3: Run the focused tests and observe RED**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter HomeShellTests
```

Expected: compile failure because `WindowNavigator` and the new Home constructor do not exist.

- [ ] **Step 4: Implement minimal navigation types**

```csharp
namespace Aerochat.Presentation;

public enum ShellRoute { Home, Chat, Settings, About, Login, ChangeScene, ImagePreviewer }
```

`WindowNavigator` stores one `PresentationState`. `Create` returns retained windows and validates payloads for Chat and ImagePreviewer. `Show` assigns `Owner` only when the target is not Home/Login, then calls `Show()`. It must not use reflection, services, process launch, or external routes.

- [ ] **Step 5: Replace application startup**

`App.OnStartup` must contain only base startup, demo-state construction, navigator construction, Home creation, `MainWindow` assignment, and `Show()`. Remove token/config loading, command-line protocols, named pipes, crash-report forking, exception transport, Discord setup, status timers, tray overlays, media playback, and fullscreen inspection.

- [ ] **Step 6: Rebind Home while preserving its XAML composition**

Keep the existing 957-line layout and visual resources. Change bindings from old view-model services to `PresentationState` properties:

- `Theme.Scene.*` to `CurrentScene.*`.
- `Categories` and `FilteredCategories` to `ContactGroups` and `FilteredContactGroups`.
- old user/presence models to `CurrentUser` and `PresenceStatus`.
- settings visibility bindings to `Settings.ShowAds`, `Settings.ShowNews`, and `Settings.ShowEyecandy`.
- remote news/notices/ad bindings to `News`, `Notices`, and `CurrentAd`.

Keep handlers only for local interactions: search, group collapse, contact/chat opening, in-memory presence and personal-message changes, local notice dismissal, local ad cycling, Settings/About/Login/scene navigation, window close, and visual hover/animation state. Delete handlers for Discord events, unread-message calculation, network fetches, update download, voice state, guild/channel mutation, external links, settings save, tray behavior, and queue processing.

- [ ] **Step 7: Make localization read-only**

Keep locale lookup and the `Loc` XAML extension. Remove any dependency on `SettingsManager`, restarts, writes, or dynamic install behavior. Default to `en-US`; process-local language changes may call `LoadLanguage(code)` and update new windows only.

- [ ] **Step 8: Verify focused tests, full suite, and build**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter HomeShellTests
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64
dotnet build Aerochat/Aerochat.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass and the app builds with 0 errors.

- [ ] **Step 9: Commit the launchable Home shell**

```bash
git add Aerochat/App.xaml.cs Aerochat/Presentation Aerochat/Windows/Home.xaml Aerochat/Windows/Home.xaml.cs Aerochat/Localization/LocalizationManager.cs Aerochat.VisualShell.Tests/WpfTestHost.cs Aerochat.VisualShell.Tests/HomeShellTests.cs
git commit -m "feat: launch populated visual Home shell"
```

### Task 5: Convert Chat to in-memory presentation behavior

**Files:**
- Create: `Aerochat.VisualShell.Tests/ChatShellTests.cs`
- Modify: `Aerochat/Presentation/PresentationState.cs`
- Modify: `Aerochat/Windows/Chat.xaml`
- Modify: `Aerochat/Windows/Chat.xaml.cs`
- Modify: `Aerochat/Controls/AttachmentsEditor/AttachmentsPreview.xaml.cs`
- Modify: `Aerochat/Controls/AttachmentsEditor/AttachmentsStrip.xaml.cs`
- Modify: `Aerochat/Controls/AttachmentsEditor/AttachmentsTinyEditor.xaml.cs`

**Interfaces:**
- Consumes: `ConversationPresentation`, `MessagePresentation`, `PresentationState`, packaged attachment resources.
- Produces: `Chat(PresentationState, ConversationPresentation, WindowNavigator)` and local reply/edit/send/attachment/drawing state transitions.

- [ ] **Step 1: Write failing Chat construction and state-transition tests**

```csharp
using Aerochat.Presentation;
using Aerochat.Windows;

namespace Aerochat.VisualShell.Tests;

public sealed class ChatShellTests
{
    [Test]
    public void Chat_constructs_with_sample_messages_without_network_client()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            ConversationPresentation conversation = state.Conversations[0];
            var chat = new Chat(state, conversation, new WindowNavigator(state));
            Assert.That(chat.DataContext, Is.SameAs(conversation));
            Assert.That(conversation.Messages, Is.Not.Empty);
            chat.Close();
        });
    }

    [Test]
    public void Reply_and_edit_change_only_local_conversation_state()
    {
        PresentationState state = DemoData.Create();
        ConversationPresentation conversation = state.Conversations[0];
        MessagePresentation target = conversation.Messages[0];

        state.BeginReply(conversation, target);
        Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.Reply));
        conversation.Draft = "Reply text";
        MessagePresentation? reply = state.SendDraft(conversation,
            new DateTimeOffset(2026, 8, 24, 12, 5, 0, TimeSpan.Zero));
        Assert.That(reply!.ReplyTo, Is.SameAs(target));

        state.BeginEdit(conversation, reply);
        conversation.Draft = "Edited locally";
        state.CommitEdit(conversation);
        Assert.That(reply.Body, Is.EqualTo("Edited locally"));
        Assert.That(conversation.TargetMode, Is.EqualTo(MessageTargetMode.None));
    }
}
```

- [ ] **Step 2: Run focused tests and observe RED**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter ChatShellTests
```

Expected: compile failures for the new constructor and reply/edit methods.

- [ ] **Step 3: Implement local message transitions**

Add `BeginReply`, `BeginEdit`, `CancelTarget`, and `CommitEdit` to `PresentationState`. `BeginEdit` copies the message body into `Draft`. `CommitEdit` ignores whitespace, updates only the selected local message, and clears target state. None of these methods may accept a service/client parameter or perform I/O.

- [ ] **Step 4: Rebind Chat while preserving the existing visual composition**

Keep the 1,917-line XAML layout, resource URIs, gradients, scene header, message pane, toolbars, input chrome, typing area, ad treatment, reply/edit bar, attachment editor, and drawing controls. Replace old bindings with `ConversationPresentation`, `PresentationState.CurrentUser`, `PresentationState.CurrentScene`, and `PresentationState.Settings`.

The code-behind must be reduced to:

- local draft send;
- enter/cancel reply mode;
- enter/commit/cancel edit mode;
- choose a conversation from sample categories;
- toggle visual toolbar and attachment-editor states;
- select packaged attachment previews;
- toggle drawing tool, undo, and redo visual flags;
- show local dialogs for intentionally inert call/game/block actions;
- normal window behavior.

Delete message fetch/send/edit/delete API calls, typing API calls, read receipts, URL/file opening, clipboard/file drop that reads arbitrary files, voice sockets, sound playback, upload/download logic, Discord cache access, and native context-menu/process code.

- [ ] **Step 5: Make attachment controls packaged-resource only**

The attachment editor accepts `PreviewImagePresentation` items from `PresentationState.PreviewImages`. Delete arbitrary file discovery, MIME inspection, shell icons, WebView handling, and downloads. Keep selection, previous/next, remove-from-local-preview, and visual strip behavior.

- [ ] **Step 6: Verify tests and build**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter ChatShellTests
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64
dotnet build Aerochat/Aerochat.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass and the app builds with 0 errors.

- [ ] **Step 7: Commit the Chat shell**

```bash
git add Aerochat/Presentation/PresentationState.cs Aerochat/Windows/Chat.xaml Aerochat/Windows/Chat.xaml.cs Aerochat/Controls/AttachmentsEditor Aerochat.VisualShell.Tests/ChatShellTests.cs
git commit -m "feat: make Chat a local visual demo"
```

### Task 6: Convert secondary windows and complete local navigation

**Files:**
- Create: `Aerochat.VisualShell.Tests/WindowNavigatorTests.cs`
- Modify: `Aerochat/Presentation/WindowNavigator.cs`
- Modify: `Aerochat/Windows/Settings.xaml` and `.xaml.cs`
- Modify: `Aerochat/Windows/About.xaml` and `.xaml.cs`
- Modify: `Aerochat/Windows/Login.xaml` and `.xaml.cs`
- Modify: `Aerochat/Windows/ChangeScene.xaml` and `.xaml.cs`
- Modify: `Aerochat/Windows/ImagePreviewer.xaml` and `.xaml.cs`
- Modify: `Aerochat/Windows/Notification.xaml` and `.xaml.cs`
- Modify: `Aerochat/Windows/Dialog.xaml` and `.xaml.cs`
- Modify: `Aerochat/Windows/ColorPicker.xaml` and `.xaml.cs`
- Modify: `Aerochat/Windows/NonNativeTooltip.xaml` and `.xaml.cs`
- Modify: `Aerochat/Windows/AerochatWindow.xaml` and `.xaml.cs`

**Interfaces:**
- Consumes: `PresentationState`, `ShellRoute`, packaged resources.
- Produces: complete `WindowNavigator.Create` coverage for all seven routes; constructors that accept presentation state instead of services.

- [ ] **Step 1: Write the failing route-construction test**

```csharp
using Aerochat.Presentation;
using Aerochat.Windows;

using System.Windows;

namespace Aerochat.VisualShell.Tests;

public sealed class WindowNavigatorTests
{
    [Test]
    public void Every_retained_route_constructs_without_backend_state()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var navigator = new WindowNavigator(state);
            var windows = new Window[]
            {
                navigator.Create(ShellRoute.Home),
                navigator.Create(ShellRoute.Chat, state.Conversations[0]),
                navigator.Create(ShellRoute.Settings),
                navigator.Create(ShellRoute.About),
                navigator.Create(ShellRoute.Login),
                navigator.Create(ShellRoute.ChangeScene),
                navigator.Create(ShellRoute.ImagePreviewer, state.PreviewImages[0])
            };

            Assert.That(windows.Select(window => window.GetType()), Is.EqualTo(new[]
            {
                typeof(Home), typeof(Chat), typeof(Settings), typeof(About),
                typeof(Login), typeof(ChangeScene), typeof(ImagePreviewer)
            }));
            foreach (Window window in windows) window.Close();
        });
    }
}
```

- [ ] **Step 2: Run the focused test and observe RED**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter WindowNavigatorTests
```

Expected: construction fails until every route has a presentation-only constructor.

- [ ] **Step 3: Convert Settings to explicit in-memory values**

Replace reflection over `SettingsManager` with a fixed presentation catalog bound to `VisualSettingsPresentation`. Category selection, checkboxes, numeric text boxes, enum selectors, and language selectors update only the state object. Language selection may call `LocalizationManager.LoadLanguage(code)` for newly opened windows. It must not write, restart, inspect devices, or spawn a process.

- [ ] **Step 4: Convert Login, About, and scene selection**

Login keeps its WLM visual layout but removes token/password/MFA/WebView behavior. Make the credential field read-only with the visible sample text `Visual shell preview`. Its primary action closes Login and shows Home through `WindowNavigator`; it does not read the field. Hyperlinks remain styled but inert. About must not launch URLs or read arbitrary files. ChangeScene binds directly to `PresentationState.Scenes` and calls `SelectScene`.

- [ ] **Step 5: Convert preview, notification, dialog, and utility windows**

ImagePreviewer accepts packaged `PreviewImagePresentation` items and supports previous/next/close only. Notification accepts a `NoticePresentation` or sample message and performs local close/timeout visuals only. Dialog accepts title, body, and a WPF `ImageSource` or local `DialogIcon` enum instead of `System.Drawing.SystemIcons`. ColorPicker and NonNativeTooltip retain local behavior. AerochatWindow remains a generic visual host.

- [ ] **Step 6: Complete `WindowNavigator`**

Use one exhaustive switch expression:

```csharp
public Window Create(ShellRoute route, object? payload = null) => route switch
{
    ShellRoute.Home => new Home(_state, this),
    ShellRoute.Chat => new Chat(_state,
        payload as ConversationPresentation ?? _state.Conversations[0], this),
    ShellRoute.Settings => new Settings(_state),
    ShellRoute.About => new About(),
    ShellRoute.Login => new Login(_state, this),
    ShellRoute.ChangeScene => new ChangeScene(_state),
    ShellRoute.ImagePreviewer => new ImagePreviewer(_state,
        payload as PreviewImagePresentation ?? _state.PreviewImages[0]),
    _ => throw new ArgumentOutOfRangeException(nameof(route), route, null)
};
```

- [ ] **Step 7: Verify focused tests, full suite, and build**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter WindowNavigatorTests
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64
dotnet build Aerochat/Aerochat.csproj -c Debug -p:Platform=x64
```

Expected: every route constructs on STA, all tests pass, app build has 0 errors.

- [ ] **Step 8: Commit secondary surfaces**

```bash
git add Aerochat/Presentation/WindowNavigator.cs Aerochat/Windows Aerochat.VisualShell.Tests/WindowNavigatorTests.cs
git commit -m "feat: connect presentation-only window navigation"
```

### Task 7: Purge backend code, dependencies, and dead projects

**Files:**
- Modify: `Aerochat.VisualShell.Tests/RepositoryLayoutTests.cs`
- Create: `Aerochat.VisualShell.Tests/ResourceIntegrityTests.cs`
- Modify: `Aerochat/Aerochat.csproj`
- Delete: every path listed in the plan's final purge section.
- Modify or delete: remaining source files flagged by the final boundary test.

**Interfaces:**
- Consumes: final retained XAML/source/resource tree.
- Produces: measurable proof that the repository contains no forbidden backend/runtime integration and every referenced packaged resource exists.

- [ ] **Step 1: Add the failing final backend-boundary test**

Add this test to `RepositoryLayoutTests`:

```csharp
[Test]
public void Product_source_contains_no_backend_or_external_side_effect_code()
{
    string[] forbiddenDirectories = ["DSP", "Aerovoice", "Aerobool", "Aerotest",
        "Installer", "Dynamic", "Aerochat/Voice", "Aerochat/Services",
        "Aerochat/Hoarder", "Aerochat/Protobuf", "Aerochat/Settings",
        "Aerochat/Theme", "Aerochat/ViewModels", "Aerochat/WebDir",
        "Aerochat/AppHostBin"];
    string[] existingDirectories = forbiddenDirectories
        .Where(path => Directory.Exists(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar))))
        .ToArray();

    string[] forbiddenTokens = ["DSharpPlus", "Aerovoice", "DiscordProtos",
        "Google.Protobuf", "WebView2", "Websocket.Client", "HttpClient",
        "ProtectedData", "NamedPipe", "Process.Start", "ShellExecute",
        "SettingsManager", "DllImport", "Vanara.PInvoke", "System.Speech"];
    var offenders = Directory.EnumerateFiles(Path.Combine(Root, "Aerochat"), "*.*",
            SearchOption.AllDirectories)
        .Where(path =>
        {
            string relative = Path.GetRelativePath(Path.Combine(Root, "Aerochat"), path);
            return !relative.StartsWith("bin" + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase)
                && !relative.StartsWith("obj" + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        })
        .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        .Select(path => new { path, text = File.ReadAllText(path) })
        .SelectMany(file => forbiddenTokens.Where(file.text.Contains)
            .Select(token => $"{Path.GetRelativePath(Root, file.path)}: {token}"))
        .ToArray();

    Assert.Multiple(() =>
    {
        Assert.That(existingDirectories, Is.Empty,
            "Forbidden directories: " + string.Join(", ", existingDirectories));
        Assert.That(offenders, Is.Empty, string.Join(Environment.NewLine, offenders));
    });
}
```

- [ ] **Step 2: Add the failing project-dependency assertion**

Read `Aerochat/Aerochat.csproj` and assert that it has no `ProjectReference` elements and that its package names equal exactly `XamlAnimatedGif`. If a retained visual control proves `System.Drawing.Common` is still required, replace that control with WPF-native `ImageSource` before changing this expected list.

- [ ] **Step 3: Add the resource-integrity test**

Parse retained `.xaml` and `.cs` files for `/Aerochat;component/<path>` and `pack://application:,,,/Aerochat;component/<path>` references. Strip query/fragment text, URL-decode the path, and assert that `Aerochat/<path>` exists with case-insensitive Windows path comparison. Also assert that every scene file named by `DemoData.Create().Scenes` and every preview/avatar/ad resource named by demo data exists.

- [ ] **Step 4: Run final boundary/resource tests and observe RED**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter "RepositoryLayoutTests|ResourceIntegrityTests"
```

Expected: FAIL on current backend directories, package/project references, source tokens, and any stale resource references.

- [ ] **Step 5: Simplify the product project**

Remove all three `ProjectReference` elements and all backend package references. Remove custom apphost configuration, copied sound assets that no longer play, WebView content, settings-generator entries, protobuf folders, and backend compile/resource declarations. Keep `UseWPF`, `net8.0-windows7.0`, x64, icon, XAML resources, scenes, ads used by demo state, locales, and `XamlAnimatedGif`.

- [ ] **Step 6: Delete backend and obsolete paths**

Delete every path listed under "Delete in the final purge". Delete any additional unreferenced backend-only helper, converter, enum, generated source, resource, or XAML file identified by the boundary test. Preserve `LICENSE`, `.editorconfig`, `.gitignore`, retained visual assets, and the design/plan documents.

- [ ] **Step 7: Resolve every stale reference**

Run the focused boundary/resource tests repeatedly. For each failure, either replace the stale reference with a retained presentation type/resource or remove the dead visual element when it belonged exclusively to a deleted backend surface. Do not weaken the forbidden token list to make the test pass.

- [ ] **Step 8: Verify final tests and build**

Run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64
dotnet build Aerochat.sln -c Debug -p:Platform=x64 --no-restore
```

Expected: all tests pass, 0 build errors, and no warnings introduced by retained visual-shell code.

- [ ] **Step 9: Commit the purge**

```bash
git add -A
git commit -m "refactor: remove Aerochat backend runtime"
```

### Task 8: Exercise the live visual shell and perform independent review

**Files:**
- Modify only if runtime or review evidence identifies a real defect.
- Review: all changes from upstream `be936ac` through the visual-shell branch.

**Interfaces:**
- Consumes: compiled `Aerochat.exe`, approved spec, this plan, complete diff.
- Produces: verified live Home/Chat/Settings/About/Login/scene/image-preview flows and one independent review result.

- [ ] **Step 1: Run final automated gates from a clean state**

```bash
git status --short
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64
dotnet build Aerochat.sln -c Debug -p:Platform=x64 --no-restore
git diff --check be936ac..HEAD
```

Expected: clean working tree, passing tests, 0 build errors, and no whitespace errors.

- [ ] **Step 2: Launch the compiled executable directly**

Run `Aerochat/bin/x64/Debug/net8.0-windows7.0/Aerochat.exe` as a tracked background process. Do not use a release from upstream and do not enter credentials.

- [ ] **Step 3: Inspect and exercise Home**

Using background computer control, verify Home shows the sample current user, scene header, Favorites/Conversations/Servers categories, representative presence frames, ad/news/notice visuals, and WLM assets. Exercise search, group collapse, status change, personal-message edit, ad cycling, notice dismissal, and scene navigation. Re-capture after each state-changing action.

- [ ] **Step 4: Inspect and exercise Chat**

Open one direct and one group conversation. Verify sample incoming/outgoing/reply/attachment messages, typing text, toolbars, input chrome, and scene styling. Exercise local send, reply, edit, cancel, attachment preview, and drawing-tool visual state. Confirm changes remain in memory only.

- [ ] **Step 5: Inspect secondary windows**

Open Settings, About, Login, ChangeScene, ImagePreviewer, Notification/Dialog where reachable. Verify controls render, local settings/scene changes affect the current process, inert links do not open a browser, and Login returns to Home without reading or storing credentials.

- [ ] **Step 6: Verify no network socket belongs to the app PID**

While the app is open, inspect `netstat -ano` for the Aerochat process ID. Expected: no established or listening TCP/UDP entries owned by Aerochat. Stop and investigate any socket before continuing.

- [ ] **Step 7: Stop the app and rerun the boundary test**

Terminate the tracked process, then run:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64 --filter Product_source_contains_no_backend_or_external_side_effect_code
```

Expected: PASS.

- [ ] **Step 8: Dispatch one independent final reviewer**

Give the reviewer the spec, plan, `git diff --stat be936ac..HEAD`, full diff, and exact test/build output. Ask for blockers first, then visual-fidelity risks, accidental backend retention, test gaps, and maintainability concerns. Require file/line citations. Do not ask the reviewer to edit.

- [ ] **Step 9: Resolve valid findings with TDD**

For every accepted behavior defect, add a failing focused test, observe the expected failure, implement the smallest fix, rerun focused/full tests, and commit a follow-up fix. Do not force-push or squash.

- [ ] **Step 10: Final verification and handoff**

```bash
git status --short
git log --oneline --decorate -10
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj -c Debug -p:Platform=x64
dotnet build Aerochat.sln -c Debug -p:Platform=x64 --no-restore
git diff --check be936ac..HEAD
```

Expected: clean working tree, all tests pass, build has 0 errors, whitespace check passes, and no push has occurred.
