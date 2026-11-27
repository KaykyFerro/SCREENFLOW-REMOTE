using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.Core.Files;

/// <summary>Alguém baixando um arquivo deste PC (para o cartão da mensagem mostrar).</summary>
public sealed record FileUploadProgress(string Share, IPAddress Remote, long Sent, long Total, bool Finished, bool Failed);

/// <summary>
/// (1.5.0) Entrega os arquivos que este PC mandou no chat (<see cref="FileShareRegistry"/>), por TCP, para quem tem a
/// mensagem (<see cref="FileTransferProtocol"/>). Lê o arquivo um bloco por vez (a memória não cresce com o tamanho do
/// arquivo), limita quantos downloads acontecem juntos e derruba quem fica parado.
/// </summary>
public sealed class FileTransferServer : IDisposable
{
    public const int MaxConnections = 12;
    public const int MaxPerAddress = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(250);
    private readonly FileShareRegistry _shares;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<IPAddress, int> _perAddress = [];
    private readonly object _gate = new();
    private TcpListener? _listener;
    private int _active;

    public FileTransferServer(FileShareRegistry shares) => _shares = shares;

    /// <summary>A porta em que está escutando (0 enquanto desligado).</summary>
    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>Quantos downloads estão acontecendo agora.</summary>
    public int Active => Volatile.Read(ref _active);

    /// <summary>Progresso de quem está baixando (chamado fora da thread da interface, no máximo 4 vezes por segundo por download).</summary>
    public event Action<FileUploadProgress>? Progress;

    /// <summary>Começa a escutar. SocketException quando a porta está em uso.</summary>
    public void Start(int port, IPAddress? bind = null)
    {
        if (_listener is not null) return;
        var listener = new TcpListener(bind ?? IPAddress.Any, port);
        // Ninguém mais pode escutar na mesma porta e pegar os pedidos.
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        listener.Start(32);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener;
        _ = AcceptLoopAsync(listener, _stop.Token);
        Log.Info($"Arquivos do chat: escutando na porta TCP {Port}.");
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        Port = 0;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (!stop.IsCancellationRequested) { continue; }
            catch (InvalidOperationException) { break; }
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            if (!TryEnter(remote))
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(client, remote, stop).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // A pessoa fechou, a rede caiu ou ficou parada: o download dela tenta de novo de onde parou.
                }
                catch (Exception exception)
                {
                    Log.Warn($"Arquivo do chat: falha entregando para {remote}: {exception.Message}");
                }
                finally
                {
                    Leave(remote);
                    client.Dispose();
                }
            }, CancellationToken.None);
        }
    }

    private bool TryEnter(IPAddress remote)
    {
        lock (_gate)
        {
            if (_active >= MaxConnections) return false;
            var count = _perAddress.GetValueOrDefault(remote);
            if (count >= MaxPerAddress) return false;
            _perAddress[remote] = count + 1;
            _active++;
            return true;
        }
    }

    private void Leave(IPAddress remote)
    {
        lock (_gate)
        {
            _active--;
            if (_perAddress.TryGetValue(remote, out var count) && count > 1) _perAddress[remote] = count - 1;
            else _perAddress.Remove(remote);
        }
    }

    private async Task ServeAsync(TcpClient client, IPAddress remote, CancellationToken stop)
    {
        client.NoDelay = true;
        client.SendBufferSize = 1024 * 1024;
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(stop);
        watchdog.CancelAfter(RequestTimeout);
        var stream = client.GetStream();
        var request = new byte[FileTransferProtocol.RequestBytes];
        await stream.ReadExactlyAsync(request, watchdog.Token).ConfigureAwait(false);
        if (!FileTransferProtocol.TryReadRequest(request, out var shareBytes, out var offset, out var clientRandom, out var token))
        {
            await Refuse(stream, FileTransferStatus.BadRequest, watchdog.Token).ConfigureAwait(false);
            return;
        }
        var share = Convert.ToHexString(shareBytes).ToLowerInvariant();
        // Sem a chave certa a resposta é a mesma de um arquivo que não existe: ninguém descobre o que este PC tem.
        if (_shares.Find(share) is not { } entry || !FileTransferProtocol.CheckToken(entry.KeyBytes(), shareBytes, offset, clientRandom, token))
        {
            await Refuse(stream, FileTransferStatus.NotFound, watchdog.Token).ConfigureAwait(false);
            return;
        }
        if (offset % FileTransferProtocol.ChunkBytes != 0 || offset > entry.Size)
        {
            await Refuse(stream, FileTransferStatus.BadRequest, watchdog.Token).ConfigureAwait(false);
            return;
        }
        FileStream file;
        try
        {
            var info = new FileInfo(entry.Path);
            if (!info.Exists || info.Length != entry.Size || info.LastWriteTimeUtc.Ticks != entry.WriteTicks) throw new IOException("mudou");
            file = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await Refuse(stream, FileTransferStatus.Changed, watchdog.Token).ConfigureAwait(false);
            return;
        }
        await using var _ = file;
        var serverRandom = RandomNumberGenerator.GetBytes(FileTransferProtocol.RandomBytes);
        await stream.WriteAsync(FileTransferProtocol.Response(FileTransferStatus.Ok, entry.Size, serverRandom), watchdog.Token).ConfigureAwait(false);
        var sessionKey = FileTransferProtocol.SessionKey(entry.KeyBytes(), shareBytes, clientRandom, serverRandom);
        using var aes = new AesGcm(sessionKey, FileTransferProtocol.TagBytes);
        var plain = ArrayPool<byte>.Shared.Rent(FileTransferProtocol.ChunkBytes);
        var frame = ArrayPool<byte>.Shared.Rent(FileTransferProtocol.FrameHeaderBytes + FileTransferProtocol.ChunkBytes + FileTransferProtocol.TagBytes);
        var nonce = new byte[FileTransferProtocol.NonceBytes];
        var sent = offset;
        var lastReport = DateTime.UtcNow;
        var finished = false;
        try
        {
            file.Position = offset;
            var count = FileTransferProtocol.ChunkCount(entry.Size);
            for (var index = offset / FileTransferProtocol.ChunkBytes; index < count; index++)
            {
                var length = FileTransferProtocol.ChunkLength(entry.Size, index);
                await file.ReadExactlyAsync(plain.AsMemory(0, length), stop).ConfigureAwait(false);
                BinaryPrimitives.WriteInt32BigEndian(frame, length);
                FileTransferProtocol.WriteNonce(index, nonce);
                aes.Encrypt(nonce, plain.AsSpan(0, length), frame.AsSpan(FileTransferProtocol.FrameHeaderBytes, length),
                    frame.AsSpan(FileTransferProtocol.FrameHeaderBytes + length, FileTransferProtocol.TagBytes));
                // Quem baixa tem até 30 segundos para aceitar cada bloco (a rede pode estar lenta, não parada).
                watchdog.CancelAfter(StallTimeout);
                await stream.WriteAsync(frame.AsMemory(0, FileTransferProtocol.FrameHeaderBytes + length + FileTransferProtocol.TagBytes), watchdog.Token).ConfigureAwait(false);
                sent += length;
                if (DateTime.UtcNow - lastReport >= ProgressEvery)
                {
                    lastReport = DateTime.UtcNow;
                    Progress?.Invoke(new FileUploadProgress(share, remote, sent, entry.Size, false, false));
                }
            }
            await stream.FlushAsync(watchdog.Token).ConfigureAwait(false);
            finished = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
            ArrayPool<byte>.Shared.Return(frame);
            Progress?.Invoke(new FileUploadProgress(share, remote, sent, entry.Size, finished, !finished));
        }
    }

    private static async Task Refuse(NetworkStream stream, FileTransferStatus status, CancellationToken token)
    {
        try { await stream.WriteAsync(FileTransferProtocol.Response(status, 0, ReadOnlySpan<byte>.Empty), token).ConfigureAwait(false); }
        catch (IOException) { }
    }
}
