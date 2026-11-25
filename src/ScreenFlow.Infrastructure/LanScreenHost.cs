using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ScreenFlow.Core;

namespace ScreenFlow.Infrastructure;

/// <summary>
/// One-viewer LAN screen-sharing host. Frame payloads are authenticated and encrypted with AES-GCM.
/// The current capture/transport path is a compatibility MVP, not the final WebRTC/GPU pipeline.
/// </summary>
public sealed class LanScreenHost : IAsyncDisposable
{
    public const int DefaultPort = 39217;
    private const int MaximumFrameBytes = 24 * 1024 * 1024;
    private readonly DisplayMonitor _monitor;
    private readonly int _maximumWidth;
    private readonly int _framesPerSecond;
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, AuthFailureWindow> _failedAttempts = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public string SessionCode { get; }
    public int Port { get; }
    public Func<string, Task<bool>>? ConnectionApproval { get; set; }
    public event Action<string>? StatusChanged;

    public LanScreenHost(DisplayMonitor monitor, int maximumWidth = 1920, int framesPerSecond = 20, int port = DefaultPort)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        if (maximumWidth is < 640 or > 1920) throw new ArgumentOutOfRangeException(nameof(maximumWidth), "O MVP LAN aceita largura máxima de 640 a 1920 pixels.");
        if (framesPerSecond is < 5 or > 30) throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "O MVP LAN aceita de 5 a 30 FPS.");
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        _maximumWidth = maximumWidth;
        _framesPerSecond = framesPerSecond;
        Port = port;
        SessionCode = SessionSecurity.CreateSessionCode();
        _listener = new TcpListener(IPAddress.Any, Port);
    }

    public void Start()
    {
        if (_worker is not null) throw new InvalidOperationException("A sessão já foi iniciada.");
        _cancellation = new CancellationTokenSource();
        _listener.Start(4);
        StatusChanged?.Invoke($"Aguardando conexão na porta {Port}.");
        _worker = AcceptLoopAsync(_cancellation.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }

            using (client)
            {
                client.NoDelay = true;
                try
                {
                    await HandleClientAsync(client, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    StatusChanged?.Invoke($"Conexão encerrada: {exception.Message}");
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        if (_failedAttempts.TryGetValue(remoteIp, out var failureWindow)
            && failureWindow.Count >= 5
            && DateTimeOffset.UtcNow - failureWindow.WindowStartedAt < TimeSpan.FromMinutes(1))
        {
            StatusChanged?.Invoke($"Tentativas limitadas temporariamente para {remoteIp}.");
            return;
        }

        var stream = client.GetStream();
        var challenge = RandomNumberGenerator.GetBytes(32);
        await stream.WriteAsync(challenge, cancellationToken);

        var proof = new byte[32];
        await stream.ReadExactlyAsync(proof, cancellationToken);
        var expectedProof = SessionSecurity.CreateAuthenticationProof(SessionCode, challenge);
        var authenticated = CryptographicOperations.FixedTimeEquals(proof, expectedProof);
        CryptographicOperations.ZeroMemory(proof);
        CryptographicOperations.ZeroMemory(expectedProof);
        if (!authenticated)
        {
            RegisterFailedAttempt(remoteIp);
            StatusChanged?.Invoke($"Tentativa de autenticação recusada: {client.Client.RemoteEndPoint}.");
            await Task.Delay(350, cancellationToken);
            return;
        }
        _failedAttempts.TryRemove(remoteIp, out _);

        var mediaKey = SessionSecurity.DeriveMediaKey(SessionCode, challenge);
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "cliente desconhecido";
        var approved = ConnectionApproval is not null && await ConnectionApproval(remote);
        await stream.WriteAsync(new[] { approved ? (byte)1 : (byte)0 }, cancellationToken);
        if (!approved)
        {
            StatusChanged?.Invoke($"Conexão rejeitada: {remote}.");
            CryptographicOperations.ZeroMemory(mediaKey);
            return;
        }

        StatusChanged?.Invoke($"Tela compartilhada com {remote}.");
        try
        {
            await StreamFramesAsync(stream, mediaKey, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mediaKey);
            StatusChanged?.Invoke("Visualizador desconectado. A sessão continua aguardando outro participante.");
        }
    }

    private async Task StreamFramesAsync(NetworkStream stream, byte[] mediaKey, CancellationToken cancellationToken)
    {
        var noncePrefix = RandomNumberGenerator.GetBytes(4);
        ulong sequence = 0;
        var interval = TimeSpan.FromSeconds(1d / _framesPerSecond);
        using var aes = new AesGcm(mediaKey, 16);
        var clock = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            var frameStarted = clock.Elapsed;
            var jpeg = await Task.Run(() => JpegScreenCapture.Capture(_monitor, _maximumWidth), cancellationToken);
            if (jpeg.Length > MaximumFrameBytes) throw new InvalidDataException("O frame excedeu o limite de segurança.");
            var nonce = new byte[12];
            noncePrefix.CopyTo(nonce, 0);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), sequence++);

            var cipher = new byte[jpeg.Length];
            var tag = new byte[16];
            aes.Encrypt(nonce, jpeg, cipher, tag);

            var packetLength = nonce.Length + cipher.Length + tag.Length;
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, packetLength);
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(nonce, cancellationToken);
            await stream.WriteAsync(cipher, cancellationToken);
            await stream.WriteAsync(tag, cancellationToken);

            var remaining = interval - (clock.Elapsed - frameStarted);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
        }
    }

    private void RegisterFailedAttempt(string remoteIp)
    {
        var now = DateTimeOffset.UtcNow;
        _failedAttempts.AddOrUpdate(
            remoteIp,
            _ => new AuthFailureWindow(now, 1),
            (_, current) => now - current.WindowStartedAt >= TimeSpan.FromMinutes(1)
                ? new AuthFailureWindow(now, 1)
                : current with { Count = current.Count + 1 });
    }

    private sealed record AuthFailureWindow(DateTimeOffset WindowStartedAt, int Count);

    public async Task StopAsync()
    {
        if (_cancellation is null) return;
        _cancellation.Cancel();
        _listener.Stop();
        if (_worker is not null)
        {
            try { await _worker; }
            catch (OperationCanceledException) { }
        }
        _cancellation.Dispose();
        _cancellation = null;
        _worker = null;
        StatusChanged?.Invoke("Compartilhamento encerrado.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}