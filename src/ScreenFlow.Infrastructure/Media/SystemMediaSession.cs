using Windows.Media;
using Windows.Storage.Streams;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.Infrastructure.Media;

/// <summary>O que o Windows mostra do player: a música (ou a transmissão), quem toca e os botões que funcionam.</summary>
public sealed record SystemMediaState(
    string Title,
    string Artist,
    string Album,
    byte[]? Cover,
    bool Playing,
    bool CanPlayPause,
    bool CanPrevious,
    bool CanNext,
    TimeSpan Position = default,
    TimeSpan Duration = default,
    bool CanSeek = false)
{
    /// <summary>Ainda conectando: o Windows mostra o player parado.</summary>
    public bool Connecting { get; init; }
}

public enum SystemMediaButton { Play, Pause, Next, Previous, Stop }

/// <summary>
/// O player do ScreenFlow como um player de música do Windows (1.3.0): aparece nos controles de mídia (o painel do
/// volume e a tela de bloqueio), responde às teclas de mídia do teclado e do fone e é reconhecido pelos apps que mostram
/// o que está tocando. Usa os controles de mídia da janela do player (SystemMediaTransportControls).
/// </summary>
public sealed class SystemMediaSession : IDisposable
{
    private readonly SystemMediaTransportControls _controls;
    private readonly object _gate = new();
    private SystemMediaState? _shown;
    private int _coverVersion;
    private bool _disposed;

    private SystemMediaSession(SystemMediaTransportControls controls)
    {
        _controls = controls;
        _controls.IsEnabled = true;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.PlaybackPositionChangeRequested += OnPositionRequested;
    }

    /// <summary>Um botão foi apertado (no painel do Windows ou numa tecla de mídia). Chamado fora da thread da interface.</summary>
    public event Action<SystemMediaButton>? ButtonPressed;

    /// <summary>Pediram para ir a outro ponto da música. Chamado fora da thread da interface.</summary>
    public event Action<TimeSpan>? SeekRequested;

    /// <summary>Liga os controles de mídia para a janela. Null se o Windows não deixar (versões muito antigas).</summary>
    public static SystemMediaSession? Create(nint window)
    {
        if (window == 0) return null;
        try { return new SystemMediaSession(SystemMediaTransportControlsInterop.GetForWindow(window)); }
        catch (Exception exception)
        {
            Log.Warn($"Controles de mídia do Windows indisponíveis para o player: {exception.Message}");
            return null;
        }
    }

    /// <summary>Atualiza o que aparece. Só mexe no que mudou (a capa só é lida de novo quando muda).</summary>
    public void Update(SystemMediaState state)
    {
        lock (_gate)
        {
            if (_disposed || state == _shown) return;
            var previous = _shown;
            _shown = state;
            try
            {
                _controls.IsPlayEnabled = state.CanPlayPause;
                _controls.IsPauseEnabled = state.CanPlayPause;
                _controls.IsNextEnabled = state.CanNext;
                _controls.IsPreviousEnabled = state.CanPrevious;
                _controls.IsStopEnabled = false;
                _controls.PlaybackStatus = state.Connecting ? MediaPlaybackStatus.Changing
                    : state.Playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

                if (previous is null || previous.Title != state.Title || previous.Artist != state.Artist || previous.Album != state.Album
                    || !ReferenceEquals(previous.Cover, state.Cover))
                {
                    var display = _controls.DisplayUpdater;
                    display.Type = MediaPlaybackType.Music;
                    display.AppMediaId = "ScreenFlow";
                    display.MusicProperties.Title = state.Title;
                    display.MusicProperties.Artist = state.Artist;
                    display.MusicProperties.AlbumTitle = state.Album;
                    display.MusicProperties.AlbumArtist = state.Album;
                    if (!ReferenceEquals(previous?.Cover, state.Cover))
                    {
                        display.Thumbnail = null;
                        if (state.Cover is { Length: > 0 } cover) _ = SetCoverAsync(cover, ++_coverVersion);
                    }
                    display.Update();
                }

                var timeline = new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    MinSeekTime = TimeSpan.Zero,
                    EndTime = state.Duration > TimeSpan.Zero ? state.Duration : TimeSpan.Zero,
                    MaxSeekTime = state.CanSeek && state.Duration > TimeSpan.Zero ? state.Duration : TimeSpan.Zero,
                    Position = state.Duration > TimeSpan.Zero ? Clamp(state.Position, state.Duration) : TimeSpan.Zero,
                };
                _controls.UpdateTimelineProperties(timeline);
            }
            catch (Exception exception)
            {
                Log.Debug($"Controles de mídia do player: {exception.Message}");
            }
        }
    }

    private static TimeSpan Clamp(TimeSpan position, TimeSpan duration) => position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;

    /// <summary>A capa vai por um stream na memória (o Windows lê quando precisa mostrar).</summary>
    private async Task SetCoverAsync(byte[] cover, int version)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(cover);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            lock (_gate)
            {
                if (_disposed || version != _coverVersion)
                {
                    stream.Dispose();
                    return;
                }
                var display = _controls.DisplayUpdater;
                display.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                display.Update();
            }
        }
        catch (Exception exception)
        {
            Log.Debug($"Capa nos controles de mídia: {exception.Message}");
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var button = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => SystemMediaButton.Play,
            SystemMediaTransportControlsButton.Pause => SystemMediaButton.Pause,
            SystemMediaTransportControlsButton.Next => SystemMediaButton.Next,
            SystemMediaTransportControlsButton.Previous => SystemMediaButton.Previous,
            SystemMediaTransportControlsButton.Stop => SystemMediaButton.Stop,
            _ => (SystemMediaButton?)null,
        };
        if (button is { } pressed) ButtonPressed?.Invoke(pressed);
    }

    private void OnPositionRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
        => SeekRequested?.Invoke(args.RequestedPlaybackPosition);

    /// <summary>Tira o ScreenFlow dos controles de mídia (o player fechou).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _controls.ButtonPressed -= OnButtonPressed;
                _controls.PlaybackPositionChangeRequested -= OnPositionRequested;
                _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
                _controls.DisplayUpdater.ClearAll();
                _controls.DisplayUpdater.Update();
                _controls.IsEnabled = false;
            }
            catch (Exception exception)
            {
                Log.Debug($"Controles de mídia do player ao fechar: {exception.Message}");
            }
        }
    }
}
