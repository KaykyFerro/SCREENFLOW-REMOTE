# Development

## Environment

- Windows 10/11 x64
- .NET 10 SDK
- Visual Studio 2022 or VS Code with C# tooling

## Build and run

    dotnet restore ScreenFlowRemote.sln
    dotnet build ScreenFlowRemote.sln --configuration Release
    dotnet test ScreenFlowRemote.sln --configuration Release
    dotnet run --project src/ScreenFlow.Remote/ScreenFlow.Remote.csproj

## LAN MVP usage

1. Run the app on the PC that will share its screen.
2. Select the monitor, maximum width and target FPS.
3. Press INICIAR COMPARTILHAMENTO.
4. Give the other PC one of the shown IPv4 addresses, TCP port 39217 and the 32-character code.
5. On the viewer, enter the IPv4 address and code, then connect.
6. Approve the incoming request on the transmitter.

Both computers must currently be on the same local network. Allow inbound TCP port 39217 only on a trusted Windows Private network.

## Current milestone

The first usable LAN path is implemented with Windows GDI capture, JPEG frames and an authenticated AES-GCM TCP stream. The capture path is a compatibility implementation and not suitable for claims of 4K/165 FPS.

## Next engineering milestones

1. Move monitor capture to Windows Graphics Capture and Direct3D 11.
2. Add GPU surface lifetime management and encoder/decoder capability detection.
3. Implement hardware H.264 encode/decode.
4. Replace LAN TCP frame transport with WebRTC media and signaling.
5. Add STUN/TURN and external-network tests.
6. Add real telemetry and adaptive quality logic.
7. Add audio, diagnostics, installer and security review.
