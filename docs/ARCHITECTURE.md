# Architecture

## Current foundation
The initial Windows desktop application is WPF. Core models are separated from Windows-specific discovery in Infrastructure. The UI currently presents monitor discovery results.

## Planned pipeline
Windows Graphics Capture -> GPU-native frame surface -> hardware encoder adapter -> WebRTC media transport -> receiver decoder -> renderer. Keep UI and pipeline work off the dispatcher thread. Prefer bounded queues and dropping stale frames over latency-growing queues.

## Network topology
A signaling service exchanges session metadata and SDP/ICE. WebRTC attempts direct connectivity and uses TURN relay when necessary. The signaling service must not proxy media by default.

## Security and consent
Explicit session acceptance, short-lived room codes, rate limits, authenticated signaling, visible active-sharing indicator, and user-controlled disconnect are baseline requirements. Remote input is deferred and must be independently consent-gated.

## Current limitation
Capture, codecs, WebRTC, audio, adaptive control, and remote input are not implemented yet. Monitor discovery is implemented using Windows display enumeration.
