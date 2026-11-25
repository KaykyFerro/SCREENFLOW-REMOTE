using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ScreenFlow.Infrastructure;

/// <summary>LAN viewer client for the ScreenFlow Remote encrypted frame protocol.</summary>
public sealed class LanScreenClient : IAsyncDisposable
{
    private const int MaximumFrameBytes = 24 * 1024 * 1024;
    private TcpClient? _client;
    private CancellationTokenSource? _cancellation;
    private Task? _reader;

    public event Action<byte[]>? FrameReceived;
    public event Action<string>? StatusChanged;
    public event Action? Disconnected;

    public async Task ConnectAsync(string host, string sessionCode, int port = LanScreenHost.DefaultPort, CancellationToken cancellationToken = default)
    {
        if (_client is not null) throw new InvalidOperationException("O cliente já está conectado.");
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Informe o IP do computador transmissor.", nameof(host));
        var normalizedCode = (sessionCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!SessionSecurity.IsValidSessionCode(normalizedCode))
            throw new ArgumentException("O código de sessão deve conter 32 caracteres hexadecimais.", nameof(sessionCode));

        _client = new TcpClient { NoDelay = true };
        try
        {
            await _client.ConnectAsync(host.Trim(), port, cancellationToken);
            var stream = _client.GetStream();
            var challenge = new byte[32];
            await stream.ReadExactlyAsync(challenge, cancellationToken);

            var proof = SessionSecurity.CreateAuthenticationProof(normalizedCode, challenge);
            await stream.WriteAsync(proof, cancellationToken);
            CryptographicOperations.ZeroMemory(proof);
            CryptographicOperations.ZeroMemory(authMaterial);

            var response = new byte[1];
            await stream.ReadExactlyAsync(response, cancellationToken);
            if (response[0] != 1)
                throw new UnauthorizedAccessException("O transmissor recusou a conexão.");

            var mediaKey = SessionSecurity.DeriveMediaKey(normalizedCode, challenge);

            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            StatusChanged?.Invoke("Conectado. Recebendo transmissão criptografada.");
            _reader = ReceiveLoopAsync(stream, mediaKey, _cancellation.Token);
        }
        catch
        {
            _client.Dispose();
            _client = null;
            throw;
        }
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, byte[] mediaKey, CancellationToken cancellationToken)
    {
        using var aes = new AesGcm(mediaKey, 16);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var header = new byte[4];
                await stream.ReadExactlyAsync(header, cancellationToken);
                var packetLength = BinaryPrimitives.ReadInt32BigEndian(header);
                if (packetLength is < 28 or > MaximumFrameBytes + 28)
                    throw new InvalidDataException("O transmissor enviou um tamanho de frame inválido.");

                var packet = new byte[packetLength];
                await stream.ReadExactlyAsync(packet, cancellationToken);
                var nonce = packet.AsSpan(0, 12);
                var cipherLength = packetLength - 28;
                var plain = new byte[cipherLength];
                aes.Decrypt(nonce, packet.AsSpan(12, cipherLength), packet.AsSpan(12 + cipherLength, 16), plain);
                FrameReceived?.Invoke(plain);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"Transmissão interrompida: {exception.Message}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mediaKey);
            StatusChanged?.Invoke("Visualizador desconectado.");
            Disconnected?.Invoke();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            _client?.Close();
            if (_reader is not null)
            {
                try { await _reader; }
                catch (OperationCanceledException) { }
            }
            _cancellation.Dispose();
            _cancellation = null;
        }
        _client?.Dispose();
        _client = null;
        _reader = null;
        StatusChanged?.Invoke("Desconectado.");
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}