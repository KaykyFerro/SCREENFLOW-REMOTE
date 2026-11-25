# Roadmap and implementation status

## Delivered in the current repository

- [x] Solution and project separation
- [x] WPF desktop dashboard
- [x] Windows monitor enumeration (resolution, primary display and position)
- [x] Refresh-rate query where exposed by the display driver
- [x] LAN host and one-viewer client
- [x] Random 128-bit session code
- [x] Challenge-response authentication
- [x] AES-GCM authenticated encryption for video frames
- [x] Explicit approval before any frame is sent
- [x] Per-IP failed-authentication throttling
- [x] GDI screen capture and JPEG frame transport for LAN MVP
- [x] Width choices up to 1920 px and target 15/20/30 FPS
- [x] Automated core tests and Windows CI build/publish workflow
- [x] Build, architecture and LAN usage documentation

## Remaining product roadmap

- [ ] Phase 2: Windows Graphics Capture, Direct3D 11 surfaces, monitor/window capture, preview pipeline
- [ ] Phase 3: H.264 hardware encode/decode, NVENC, AMF and Quick Sync detection
- [ ] Phase 4: Replace JPEG/TCP with real-time media transport
- [ ] Phase 5: WebRTC signaling, ICE, STUN/TURN and cross-network connectivity
- [ ] Phase 6: Real telemetry-driven adaptive bitrate, resolution and FPS control
- [ ] Phase 7: HEVC/AV1 capability negotiation and fallback
- [ ] Phase 8: system audio capture and synchronization
- [ ] Phase 9: security review, session expiry, additional abuse protection and audit
- [ ] Phase 10: diagnostics, settings, installer, updater and release packaging
- [ ] Phase 11: separately authorized remote mouse and keyboard input

## Performance target

4K UHD and 165 FPS remain architectural goals only. No such throughput has been measured or validated. The current CPU-based GDI/JPEG LAN MVP is intentionally capped at 1920 pixels wide and a requested maximum of 30 FPS.
