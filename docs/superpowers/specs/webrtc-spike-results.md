# Task 12: SIPSorcery WebRTC feasibility spike

Date: 2026-08-25
Runners: Codex CLI worker (sandboxed, could not reach nuget.org) + orchestrator verification (unsandboxed).

## Scope and time box

Time-boxed feasibility spike in isolated `task12-rtc-spike` worktree. Scratch projects under `spike/`, not part of `Aerochat.sln`. No files under `Aerochat/`, `Aerochat.Server/`, or the test projects were changed.

## Package matrix (verified against nuget.org flatcontainer API)

| Package | Last net8-compatible | Notes |
|---|---|---|
| `SIPSorcery` | **8.0.14** (8.x line) | 8.0.23 exists but pulls nothing newer for net8 media; **GHSA-jwjp-4649-v8jp (high, SCTP SACK OOB DoS)** affects <= 10.0.13, first patched in 10.0.14 (.NET 10 only); 8.0.14 additionally flagged by **GHSA-28gm-jrmw-xx93 (high)** |
| `SIPSorceryMedia.Windows` | **8.0.14** | requires TFM >= `net8.0-windows10.0.17763`; every 10.x release targets net10.0 only |
| `SIPSorceryMedia.Encoders` | **8.0.14** | the net8-era VP8/Vorbis encoder companion (`SIPSorcery.VP8` standalone package is net10-only, all versions) |
| `SIPSorcery.VP8` | none | net10.0 only across its entire published range |

## Build verification (performed by orchestrator, real nuget.org access)

| Probe | Pin | TFM | Result |
|---|---|---|---|
| `spike/AudioLoopback` | SIPSorcery 8.0.14 + SIPSorceryMedia.Windows 8.0.14 | net8.0-windows10.0.17763 | **compiles, 0 errors** |
| `spike/ScreenCaptureVp8` | SIPSorcery 8.0.14 + SIPSorceryMedia.Encoders 8.0.14 | net8.0-windows10.0.19041 | **compiles, 0 errors** |
| `spike/PeerRelay` | (WebSocket relay, no RTC pkg) | net8.0 | **compiles, 0 errors** |

## Results

### 1. Microphone loopback — PARTIALLY VERIFIED (build-level)

Worker sandbox could not restore any package (NU1301 SSL/Schannel failure inside Codex workspace-write sandbox — environment issue, not SIPSorcery). After pinning 8.0.14 and raising the TFM, the probe compiles against the real packages. **Runtime mic->Opus->speaker behavior was NOT exercised on this host** (no audio device run was performed). Reproduce: `dotnet run --project spike/AudioLoopback -c Debug`.

### 2. Windows.Graphics.Capture -> VP8 — PARTIALLY VERIFIED (build-level)

Same restore story. Compiles with `SIPSorceryMedia.Encoders` 8.0.14 providing VP8 encoding (the plan's assumed `SIPSorcery.VP8` dependency does not exist for net8). Runtime capture/encode NOT exercised. Reproduce: `dotnet run --project spike/ScreenCaptureVp8 -c Debug`.

### 3. Two peers over localhost WebSocket relay — PARTIALLY VERIFIED (build-level)

Relay console compiles. The offer/answer exchange between two SIPSorcery peer processes was NOT executed end-to-end on this host.

## Verdict

**CONDITIONAL PROCEED** — SIPSorcery is viable for Phases 3 (Tasks 13–16) on .NET 8 today, with three owner-visible caveats:

1. **Known unpatched-on-net8 advisories.** Every net8-compatible SIPSorcery release carries high-severity advisories patched only in the .NET 10 line (SCTP SACK OOB read among them). Exposure is primarily malformed-packet DoS; acceptable for a self-hosted friends-scale v1 if documented, or defer client RTC until a .NET 10 upgrade.
2. **TFM raise required.** RTC work forces the WPF client from `net8.0-windows7.0` to at least `net8.0-windows10.0.17763`. This is a client-wide change and must be gated through the visual smoke suite.
3. **Encoder companion swap.** Use `SIPSorceryMedia.Encoders` 8.0.x for VP8; the plan's named `SIPSorcery.VP8` package is net10-only.

Task 13 (call signaling state machine) carries none of these risks — it is pure server-side data and can proceed immediately.

## Exact pins that work

```xml
<PackageReference Include="SIPSorcery" Version="8.0.14" />
<PackageReference Include="SIPSorceryMedia.Windows" Version="8.0.14" />   <!-- TFM net8.0-windows10.0.17763+ -->
<PackageReference Include="SIPSorceryMedia.Encoders" Version="8.0.14" />  <!-- VP8 on net8 -->
```
