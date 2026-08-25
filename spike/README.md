# Task 12 WebRTC feasibility spike

These scratch projects are deliberately outside `Aerochat.sln` and are not product code.

- `AudioLoopback`: intended microphone -> Opus -> decode -> speaker loopback.
- `ScreenCaptureVp8`: intended Windows.Graphics.Capture -> VP8 -> frame dump/render.
- `PeerRelay`: intended two localhost console peers with a trivial WebSocket relay.

The projects use the .NET 8 package line selected for this spike (`8.0.23`). The package restore was attempted but was blocked by the execution environment's Windows Schannel credentials error; see `docs/superpowers/specs/webrtc-spike-results.md`.
