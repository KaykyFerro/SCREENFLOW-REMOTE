# Troubleshooting

## Build problems

- Install the .NET 10 SDK.
- Build on Windows because WPF and Windows Forms targets require Windows desktop targeting.
- Run dotnet restore ScreenFlowRemote.sln before building.

## Connection problems

- The current MVP works only between PCs on the same LAN.
- Use the transmitter's IPv4 address and port 39217.
- Allow inbound TCP 39217 on the transmitter only for a trusted Private network.
- Ensure the transmitter started a session and has not closed the app.
- Use the current session code; a new host instance generates a new code.
- The transmitter must approve the incoming connection.

## Black or stale image

- Confirm the selected monitor is still connected.
- Stop and recreate the session after changing display topology.
- Lower the target FPS or maximum width on older CPUs.
- The current GDI/JPEG path is CPU-based; it is not the future GPU capture implementation.

## Known missing capabilities

Internet/NAT traversal, WebRTC, STUN/TURN, audio, hardware video codecs, adaptive bitrate, remote input, installer and validated 4K/165 FPS operation are not yet implemented.
