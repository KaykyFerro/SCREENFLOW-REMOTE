# Technical Decisions

- **WPF**: chosen for mature Windows desktop support and rapid implementation of a native desktop dashboard.
- **.NET 10**: target selected, subject to installed SDK and dependency validation on build environment.
- **Separation**: Core model and Windows-specific Infrastructure are separate projects; UI consumes services.
- **Display enumeration**: System.Windows.Forms Screen enumeration with Win32 EnumDisplaySettings for current refresh-rate reporting.
- **Capture roadmap**: Windows Graphics Capture with Direct3D 11 surfaces, avoiding repeated CPU screenshots.
- **Transport roadmap**: WebRTC, with signaling hosted separately and STUN/TURN deployment configurable.
- **Media strategy**: hardware encoder first where supported; fallback options must be detected rather than assumed.
- **Metrics**: only measurements collected from actual pipeline/network/hardware are displayed; no synthetic performance claims.
