# Technical Decisions

- **WPF**: chosen for mature Windows desktop support and native Windows integration.
- **.NET 10**: target framework; Windows CI restores and builds the solution.
- **Separation**: Core model and security primitives are separate from Windows-specific Infrastructure; UI consumes services.
- **Display enumeration**: System.Windows.Forms Screen enumeration with Win32 EnumDisplaySettings for current refresh-rate reporting where available.
- **LAN compatibility path**: GDI capture and JPEG encoding were selected as a first end-to-end MVP so two PCs can share a screen before the GPU capture pipeline exists.
- **LAN transport**: TCP is used for the compatibility MVP. Each session has a random 128-bit hexadecimal code; authentication uses a fresh challenge and HMAC-SHA256; media frames are protected with AES-GCM. The viewer must be approved by the transmitter.
- **MVP bounds**: 640–1920 px maximum width and 5–30 requested FPS at the service layer; the UI offers 1280/1600/1920 and 15/20/30. This is not a performance guarantee.
- **Capture roadmap**: Windows Graphics Capture with Direct3D 11 surfaces, avoiding repeated CPU screenshots.
- **Transport roadmap**: WebRTC with a separate signaling service, ICE negotiation and configurable STUN/TURN.
- **Media strategy**: hardware encoders/decoders first where supported; fallback options must be detected rather than assumed.
- **Metrics**: only measurements collected from an actual pipeline/network/hardware are displayed; no synthetic performance claims.
- **Consent**: no frame is sent before an explicit approval dialog on the transmitting PC. Remote input remains out of scope until separately authorized.
