using Windows.Media.Control;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Infrastructure.Media;

/// <summary>Um app tocando mídia no Windows (Spotify, YouTube no navegador...), como aparece nos controles de mídia.</summary>
public sealed record MediaSessionInfo(
    string Id,
    string AppId,
    string AppName,
    string Title,
    string Artist,
    PlaybackTimeline Timeline,
    bool CanPrevious,
    bool CanNext,
    bool CanPlayPause,
    bool CanSeek,
    byte[]? Thumbnail)
{
    public bool Playing => Timeline.Playing;
}

public enum MediaCommand { PlayPause, Previous, Next }

/// <summary>
/// Os controles de mídia do Windows (os mesmos das teclas de mídia e do painel de volume): lista quem está tocando e
/// manda voltar, pausar, pular ou ir para um ponto da música ou do vídeo. Os apps avisam quando algo muda; a lista é
/// relida nesses avisos e a cada 2 segundos.
/// </summary>
public sealed class MediaControls : IDisposable
{
    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private readonly object _gate = new();
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _watched = [];
    private readonly Dictionary<string, (string Key, byte[]? Image)> _thumbnails = [];
    private readonly System.Threading.Timer _timer;
    private IReadOnlyList<MediaSessionInfo> _sessions = [];
    private Dictionary<string, GlobalSystemMediaTransportControlsSession> _byId = [];
    private string? _currentId;
    private int _refreshing;
    private volatile bool _dirty;
    private bool _disposed;

    private MediaControls(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        _manager = manager;
        _manager.SessionsChanged += OnManagerChanged;
        _manager.CurrentSessionChanged += OnManagerChanged;
        _timer = new System.Threading.Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    /// <summary>Abre os controles de mídia. Null se o Windows não tiver (Windows 10 1809 ou mais novo).</summary>
    public static async Task<MediaControls?> OpenAsync()
    {
        try { return new MediaControls(await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()); }
        catch (Exception exception)
        {
            Log.Warn($"Controles de mídia do Windows indisponíveis: {exception.Message}");
            return null;
        }
    }

    /// <summary>Quem está tocando (ou pausado) agora.</summary>
    public IReadOnlyList<MediaSessionInfo> Sessions
    {
        get { lock (_gate) return _sessions; }
    }

    /// <summary>O que as teclas de mídia do teclado controlariam agora.</summary>
    public string? CurrentId
    {
        get { lock (_gate) return _currentId; }
    }

    /// <summary>A lista mudou (chamado fora da thread da interface).</summary>
    public event Action? Changed;

    /// <summary>
    /// O que mostrar: o app que está sendo transmitido (janela ou festa com um app só), se ele estiver nos controles; senão
    /// o que as teclas de mídia controlariam; senão o primeiro tocando.
    /// </summary>
    public MediaSessionInfo? Pick(string? preferredExe, string? keepId = null)
    {
        var sessions = Sessions;
        if (keepId is not null && sessions.FirstOrDefault(s => s.Id == keepId) is { } kept) return kept;
        if (preferredExe is not null && sessions.FirstOrDefault(s => MediaApps.Matches(s.AppId, preferredExe)) is { } shared) return shared;
        var current = CurrentId;
        return sessions.FirstOrDefault(s => s.Id == current) ?? sessions.FirstOrDefault(s => s.Playing) ?? sessions.FirstOrDefault();
    }

    public async Task<bool> SendAsync(string id, MediaCommand command)
    {
        var session = Find(id);
        if (session is null) return false;
        try
        {
            return command switch
            {
                MediaCommand.Previous => await session.TrySkipPreviousAsync(),
                MediaCommand.Next => await session.TrySkipNextAsync(),
                _ => await session.TryTogglePlayPauseAsync(),
            };
        }
        catch (Exception exception)
        {
            Log.Warn($"Comando de mídia {command} falhou: {exception.Message}");
            return false;
        }
        finally { Refresh(); }
    }

    /// <summary>Vai para <paramref name="position"/> da música ou do vídeo.</summary>
    public async Task<bool> SeekAsync(string id, TimeSpan position)
    {
        var session = Find(id);
        if (session is null) return false;
        try
        {
            var timeline = session.GetTimelineProperties();
            var ticks = Math.Clamp((timeline.StartTime + position).Ticks, timeline.StartTime.Ticks, Math.Max(timeline.StartTime.Ticks, timeline.EndTime.Ticks));
            return await session.TryChangePlaybackPositionAsync(ticks);
        }
        catch (Exception exception)
        {
            Log.Warn($"Mudar a posição da mídia falhou: {exception.Message}");
            return false;
        }
        finally { Refresh(); }
    }

    private GlobalSystemMediaTransportControlsSession? Find(string id)
    {
        lock (_gate) return _byId.GetValueOrDefault(id);
    }

    private void OnManagerChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args) => Refresh();

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => Refresh();

    /// <summary>Relê a lista (uma leitura por vez; um aviso no meio dela faz outra logo depois).</summary>
    private void Refresh()
    {
        _dirty = true;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (_dirty && !_disposed)
                {
                    _dirty = false;
                    await ReadAsync();
                }
            }
            catch (Exception exception) { Log.Debug($"Controles de mídia: {exception.Message}"); }
            finally { Volatile.Write(ref _refreshing, 0); }
            if (_dirty && !_disposed) Refresh();
        });
    }

    private async Task ReadAsync()
    {
        var sessions = _manager.GetSessions();
        var current = _manager.GetCurrentSession();
        var list = new List<MediaSessionInfo>();
        var byId = new Dictionary<string, GlobalSystemMediaTransportControlsSession>();
        string? currentId = null;
        foreach (var session in sessions)
        {
            var appId = session.SourceAppUserModelId ?? string.Empty;
            var id = appId;
            for (var n = 2; byId.ContainsKey(id); n++) id = $"{appId}#{n}";
            byId[id] = session;
            Watch(id, session);
            if (ReferenceEquals(session, current) || (current is not null && currentId is null && current.SourceAppUserModelId == appId)) currentId = id;

            string title = string.Empty, artist = string.Empty;
            byte[]? thumbnail = null;
            try
            {
                var properties = await session.TryGetMediaPropertiesAsync();
                if (properties is not null)
                {
                    title = properties.Title ?? string.Empty;
                    artist = properties.Artist is { Length: > 0 } a ? a : properties.AlbumArtist ?? string.Empty;
                    thumbnail = await ThumbnailAsync(id, $"{title}\n{artist}", properties.Thumbnail);
                }
            }
            catch (Exception exception) { Log.Debug($"Sem os dados da mídia de {appId}: {exception.Message}"); }

            var playback = session.GetPlaybackInfo();
            var controls = playback?.Controls;
            var timeline = session.GetTimelineProperties();
            var playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var duration = timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime;
            var position = timeline is null ? TimeSpan.Zero : timeline.Position - timeline.StartTime;
            var updatedAt = timeline?.LastUpdatedTime ?? DateTimeOffset.UtcNow;
            // Alguns apps não mandam a hora do aviso: conta a partir de agora.
            if (updatedAt.Year < 2000) updatedAt = DateTimeOffset.UtcNow;
            list.Add(new MediaSessionInfo(id, appId, MediaApps.DisplayName(appId), title, artist,
                new PlaybackTimeline(position, duration < TimeSpan.Zero ? TimeSpan.Zero : duration, updatedAt, playing),
                controls?.IsPreviousEnabled == true, controls?.IsNextEnabled == true,
                controls is null || controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                controls?.IsPlaybackPositionEnabled == true && duration > TimeSpan.Zero,
                thumbnail));
        }
        List<GlobalSystemMediaTransportControlsSession> closed;
        lock (_gate)
        {
            if (_disposed) return;
            _sessions = list;
            _byId = byId;
            _currentId = currentId;
            foreach (var gone in _thumbnails.Keys.Where(k => !byId.ContainsKey(k)).ToList()) _thumbnails.Remove(gone);
            var goneIds = _watched.Keys.Where(k => !byId.ContainsKey(k)).ToList();
            closed = goneIds.Select(k => _watched[k]).ToList();
            foreach (var k in goneIds) _watched.Remove(k);
        }
        foreach (var session in closed) Unwatch(session);
        Changed?.Invoke();
    }

    /// <summary>Assina os avisos de um app (uma vez; se o app reabriu a sessão, troca para a nova).</summary>
    private void Watch(string id, GlobalSystemMediaTransportControlsSession session)
    {
        GlobalSystemMediaTransportControlsSession? old;
        lock (_gate)
        {
            if (_disposed) return;
            if (_watched.TryGetValue(id, out old) && ReferenceEquals(old, session)) return;
            _watched[id] = session;
        }
        if (old is not null) Unwatch(old);
        try
        {
            session.PlaybackInfoChanged += OnSessionChanged;
            session.MediaPropertiesChanged += OnSessionChanged;
            session.TimelinePropertiesChanged += OnSessionChanged;
        }
        catch (Exception exception) { Log.Debug($"Sem avisos da mídia de {id}: {exception.Message}"); }
    }

    private void Unwatch(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.PlaybackInfoChanged -= OnSessionChanged;
            session.MediaPropertiesChanged -= OnSessionChanged;
            session.TimelinePropertiesChanged -= OnSessionChanged;
        }
        catch { }
    }

    /// <summary>A capa (lida só quando a música muda).</summary>
    private async Task<byte[]?> ThumbnailAsync(string id, string key, Windows.Storage.Streams.IRandomAccessStreamReference? reference)
    {
        lock (_gate)
        {
            if (_thumbnails.TryGetValue(id, out var cached) && cached.Key == key) return cached.Image;
        }
        byte[]? image = null;
        if (reference is not null)
        {
            try
            {
                using var stream = await reference.OpenReadAsync();
                if (stream.Size is > 0 and < 8_000_000)
                {
                    using var source = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream);
                    using var memory = new MemoryStream();
                    await source.CopyToAsync(memory);
                    image = memory.ToArray();
                }
            }
            catch (Exception exception) { Log.Debug($"Sem a capa da mídia: {exception.Message}"); }
        }
        lock (_gate) _thumbnails[id] = (key, image);
        return image;
    }

    public void Dispose()
    {
        List<GlobalSystemMediaTransportControlsSession> watched;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watched = [.. _watched.Values];
            _watched.Clear();
        }
        _timer.Dispose();
        try
        {
            _manager.SessionsChanged -= OnManagerChanged;
            _manager.CurrentSessionChanged -= OnManagerChanged;
        }
        catch { }
        foreach (var session in watched) Unwatch(session);
    }
}
