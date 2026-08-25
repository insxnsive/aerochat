# Desktop Authentication

The WPF client performs OAuth through the self-hosted Aerochat server. It never embeds a provider login page and never receives or stores provider client secrets.

## Configuration and offline behavior

Set the server origin through:

```text
AEROCHAT_SERVER_URL=https://chat.example.com
```

The value must be an absolute HTTP or HTTPS origin with only the root path and without user information, query, or fragment. Invalid or missing values select `NullAuthClient`; the application still launches in DemoData mode and Login shows Google, GitHub, and Discord as disabled with `Server not configured.`

No browser or network action occurs until the user explicitly clicks an enabled provider button.

## Login flow

1. `OAuthAuthClient` creates and starts a loopback callback listener on `127.0.0.1` using an OS-assigned port.
2. It builds `/auth/{provider}/start?returnUri=<loopback callback>` under the configured server origin.
3. `ShellBrowserLauncher` opens that server URL through the Windows shell only after the explicit button click.
4. The callback listener accepts only `GET /oauth/callback?code=<handoff>` using HTTP/1.1.
5. The client posts the handoff to `/auth/session/exchange` and validates the returned nonempty token and positive expiration.
6. If Remember Me is checked, the session token is saved through DPAPI. If unchecked, any previously persisted token is removed and the new token stays in memory only.

Expected browser, socket, filesystem, DPAPI, and platform-launch failures are normalized to `AuthException`. Internal OAuth and HTTP timeouts are also typed auth failures; explicit caller/window cancellation remains cancellation. Login catches only those expected errors, cancels an active flow when the window closes, and never lets a late callback navigate after dismissal. Programming failures are not swallowed.

## Loopback listener hardening

`LoopbackCallbackListener`:

- binds only IPv4 loopback (`127.0.0.1`)
- remains bound while the browser is open, avoiding a port-rebinding race
- limits request headers to 16 KiB
- gives each local connection a one-second header-read budget
- accepts local connections concurrently, so a partial/stalled request cannot block the real callback
- accepts only the exact callback path, GET, HTTP/1.1, and a nonempty `code`
- never echoes the handoff code in its HTML response
- stops all pending accepts/reads when login completes, cancels, or is disposed

## Session cache

`DpapiTokenCache` encrypts session tokens with Windows DPAPI using `DataProtectionScope.CurrentUser` and fixed application entropy.

Default location:

```text
%LOCALAPPDATA%\Aerochat\session.bin
```

Writes are serialized across cache instances, use unique same-directory temporary files, and are atomically moved into place. Corrupt, tampered, or invalid-UTF-8 cache files are removed and treated as an empty cache. Tokens are never logged.

## Layering

All client network, browser-launch, loopback, and DPAPI code lives under:

```text
Aerochat/Connectivity/
```

`Aerochat.Presentation` stays free of networking types. `WindowNavigator` receives a Login factory, and `App.xaml.cs` is the composition root that chooses real or null authentication.

Login keeps the WLM 2009 titlebar, profile frame, presence selector, Remember Me control, nine-slice panel, and visual spacing. Legacy Discord-token, password, MFA, and reset-password controls were removed.

## Source and tests

```text
Aerochat/Connectivity/ITokenCache.cs
Aerochat/Connectivity/DpapiTokenCache.cs
Aerochat/Connectivity/Auth/IAuthClient.cs
Aerochat/Connectivity/Auth/OAuthAuthClient.cs
Aerochat/Connectivity/Auth/LoopbackCallbackListener.cs
Aerochat/Connectivity/Auth/ShellBrowserLauncher.cs
Aerochat/Connectivity/Auth/NullAuthClient.cs
Aerochat/App.xaml.cs
Aerochat/Presentation/WindowNavigator.cs
Aerochat/Windows/Login.xaml
Aerochat/Windows/Login.xaml.cs
Aerochat.VisualShell.Tests/ConnectivityTests.cs
Aerochat.VisualShell.Tests/WindowNavigatorTests.cs
Aerochat.VisualShell.Tests/RepositoryLayoutTests.cs
```

Focused verification:

```bash
dotnet test Aerochat.VisualShell.Tests/Aerochat.VisualShell.Tests.csproj \
  -c Debug -p:Platform=x64 --no-restore \
  --filter "FullyQualifiedName~ConnectivityTests|FullyQualifiedName~WindowNavigatorTests"
```

The runtime visual smoke launches the actual Login window with `NullAuthClient`, verifies disabled provider controls and the offline status, and confirms the process has no socket rows.
