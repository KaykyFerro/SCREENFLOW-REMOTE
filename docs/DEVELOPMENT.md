# Development

## Environment
Windows 10/11 x64 and .NET 10 SDK.

## Build and run
```powershell
dotnet restore ScreenFlowRemote.sln
dotnet build ScreenFlowRemote.sln
dotnet run --project src/ScreenFlow.Remote/ScreenFlow.Remote.csproj
```

## First milestone
The dashboard enumerates monitors and displays resolution, primary status, position and refresh-rate information where the Windows display driver exposes it.

## Next milestones
1. Add tests and diagnostics.
2. Implement Windows Graphics Capture and preview.
3. Establish GPU surface management and encoder capability detection.
4. Implement WebRTC transport and signaling service.
5. Add adaptive bitrate/quality controller driven by real telemetry.
