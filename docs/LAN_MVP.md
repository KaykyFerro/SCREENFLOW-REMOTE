# LAN MVP (current implemented transport)

## What it does

The current desktop app supports one-way screen viewing between two Windows PCs on the same local network:
1. On the transmitting PC, select a monitor and start a session.
2. Share the displayed IPv4 address, TCP port 39217, and 32-character session code with the viewer.
3. On the viewer PC, enter the address and code and connect.
4. The transmitting PC shows an explicit approval prompt. No frames are sent before approval.
5. Stop the session or disconnect from either side to end viewing.

The session code is 128 bits of cryptographically generated randomness. The client proves possession of the code using a fresh server challenge. Frame payloads are encrypted and authenticated with AES-GCM. The session key is derived separately from the authentication proof.

## Current technical limits

This is a functional compatibility MVP, not the target streaming pipeline:
- Windows GDI screen capture and JPEG encoding are used.
- Configurable maximum width: 1280, 1600 or 1920 pixels.
- Configurable target rate: 15, 20 or 30 FPS.
- One viewer at a time.
- TCP transport over the LAN.
- No audio.
- No remote mouse or keyboard control.
- No automatic Internet/NAT traversal.
- No STUN, TURN or WebRTC yet.
- These settings are requests/limits, not guaranteed achieved throughput.

The GDI capture path is CPU-based and may use significant CPU at high resolutions. It is intentionally bounded so this MVP does not advertise 4K/165 FPS support it has not demonstrated.

## Firewall

Windows may ask whether ScreenFlow Remote can accept incoming connections. Allow it only on trusted private networks. If configuring a rule manually, allow inbound TCP port 39217 only on the Private profile.

## Troubleshooting

- Confirm both PCs are connected to the same LAN.
- Use the transmitter's IPv4 address, not 127.0.0.1.
- Check that TCP 39217 is allowed through the transmitter's firewall on a trusted Private network.
- The session code changes every time a new host instance is created.
- The transmitter must approve the viewer's connection.
- If the selected monitor is disconnected or changed, stop and create a new session.

## Security notes

The session code is a bearer secret. Share it only with the intended viewer. The app generates a fresh challenge per connection, validates the HMAC proof using constant-time comparison, and encrypts frame data with AES-GCM. The current LAN MVP has not undergone an external security audit. Do not expose its listening port directly to the public Internet.
