# Troubleshooting

- **SDK missing**: install the .NET 10 SDK and retry.
- **Monitor refresh rate unavailable**: some drivers do not expose a useful current frequency via display settings; this is shown as unavailable rather than fabricated.
- **No monitor list**: confirm the Windows session is interactive and the display driver is active.
- **Build on non-Windows**: WPF and Windows Forms projects require Windows targeting; build on Windows for runtime validation.

Streaming and network troubleshooting guides will be expanded when those subsystems exist.
