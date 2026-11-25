# Architecture

## Current implementation

- ScreenFlow.Remote: WPF desktop application and user consent dialogs.
- ScreenFlow.Core: transport-independent display model.
- ScreenFlow.Infrastructure: Windows monitor enumeration, GDI compatibility capture, LAN host/viewer transport and local address discovery.
- ScreenFlow.Tests: unit tests for core models.

## Current LAN MVP data flow

Windows Screen -> GDI capture -> JPEG encode -> AES-GCM -> TCP/LAN -> AES-GCM decrypt -> WPF image renderer

The host supports one viewer at a time. Session codes contain 128 bits of random entropy. A fresh challenge is used for HMAC-based authentication; media encryption uses a separate derived key. The host requires explicit approval before sending frames.

## Target high-performance data flow

Windows Graphics Capture -> Direct3D 11 GPU surface -> hardware encoder (NVENC / AMF / Quick Sync) -> WebRTC SRTP media -> hardware decoder -> GPU renderer

The target design uses bounded queues and drops stale frames rather than allowing latency to grow. Capture, encoding, transport, decoding and rendering must not run on the UI dispatcher.

## Network topology target

A signaling service exchanges session metadata and SDP/ICE candidates. WebRTC attempts a direct path first and uses TURN relay when necessary. The signaling service must not proxy media by default.

## Security and consent

Explicit session acceptance, short-lived room codes, rate limits, authenticated signaling, encrypted transport, visible active-sharing state, and user-controlled disconnect are baseline requirements. Remote input remains deferred and must be separately consent-gated.

## Current limitations

The LAN MVP is not the final streaming architecture. It uses CPU-based GDI/JPEG capture, supports up to 1920 pixels wide and 30 requested FPS, and only supports same-LAN TCP connectivity. It does not currently implement WGC, Direct3D frame sharing, hardware encoding, WebRTC, STUN/TURN, cross-network connection, audio, or remote input. No 4K/165 FPS performance has been validated.
