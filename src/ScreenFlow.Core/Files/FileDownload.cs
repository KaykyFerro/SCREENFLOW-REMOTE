using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Security;

namespace ScreenFlow.Core.Files;

public enum FileDownloadState { Waiting, Connecting, Downloading, Verifying, Completed, Paused, Failed, Canceled }

/// <summary>Onde um download parou (guardado cifrado, para continuar depois, mesmo com o PC desligado no meio).</summary>
internal sealed record FileDownloadCheckpoint(ChatFile File, string Destination, long Confirmed, DateTimeOffset Updated);

/// <summary>
/// (1.5.0) O download de um arquivo do chat, direto do PC de quem mandou. Grava num "<c>.sfpart</c>" ao lado do destino
/// (nada de copiar gigabytes de uma pasta para outra no fim), confere cada bloco (AES-GCM) e o arquivo inteiro (SHA-256)
/// antes de dar o nome de verdade, e continua de onde parou: se a conexão cair, tenta de novo sozinho; se você pausar
/// ou fechar o ScreenFlow, continua depois. Nunca abre o arquivo: no Windows, ele ganha a marca de "veio da internet",
/// então o próprio Windows avisa antes de rodar um programa baixado.
/// </summary>
public sealed class FileDownload
{
    public const string PartSuffix = ".sfpart";
    private const int MaxAttempts = 6;
    private const long CheckpointBytes = 8L * 1024 * 1024;
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChangedEvery = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly string _statePath;
    private readonly byte[] _storeKey;
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private Task? _task;
    private volatile bool _cancelRequested;
    private DateTime _lastChanged;

    public FileDownload(ChatFile file, string destination, string stateFolder, byte[] storeKey)
    {
        if (!file.IsValid) throw new ArgumentException("Arquivo inválido.", nameof(file));
        if (storeKey.Length != 32) throw new ArgumentException("A chave precisa ter 32 bytes.", nameof(storeKey));
        File = file;
        Destination = Path.GetFullPath(destination);
        _statePath = StatePath(stateFolder, file.Share);
        _storeKey = storeKey;
    }

    public ChatFile File { get; }

    /// <summary>Onde o arquivo fica quando terminar (o que você escolheu).</summary>
    public string Destination { get; }

    public string PartPath => Destination + PartSuffix;

    public FileDownloadState State { get; private set; } = FileDownloadState.Waiting;

    public long Received { get; private set; }

    public double BytesPerSecond { get; private set; }

    /// <summary>Por que parou (null quando está tudo certo).</summary>
    public string? Error { get; private set; }

    public bool IsRunning => _task is { IsCompleted: false };

    /// <summary>Quanto esperar antes da tentativa <c>n</c> (2, 4, 8, 16 s; os testes encurtam).</summary>
    internal Func<int, TimeSpan> RetryDelay { get; init; } = attempt => TimeSpan.FromSeconds(Math.Min(16, 1 << attempt));

    /// <summary>Mudou o estado ou o progresso (fora da thread da interface; o progresso no máximo 4 vezes por segundo).</summary>
    public event Action<FileDownload>? Changed;

    public static string StatePath(string folder, string share) => Path.Combine(folder, share + ".state");

    /// <summary>Um download que tinha parado (o ScreenFlow fechou no meio): volta pausado, pronto para continuar.</summary>
    public static FileDownload? Restore(string statePath, byte[] storeKey)
    {
        try
        {
            var records = new EncryptedLog(statePath, storeKey).ReadAll(1);
            if (records.Count == 0) return null;
            var checkpoint = JsonSerializer.Deserialize<FileDownloadCheckpoint>(records[0], Options);
            if (checkpoint is null || !checkpoint.File.IsValid || string.IsNullOrWhiteSpace(checkpoint.Destination)) return null;
            if (Path.GetFileName(statePath) != checkpoint.File.Share + ".state") return null;
            var download = new FileDownload(checkpoint.File, checkpoint.Destination, Path.GetDirectoryName(statePath)!, storeKey)
            {
                State = FileDownloadState.Paused,
                Received = Math.Clamp(checkpoint.Confirmed, 0, checkpoint.File.Size),
            };
            return download;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        {
            Log.Warn($"Download parado não abriu ({Path.GetFileName(statePath)}): {exception.Message}");
            return null;
        }
    }

    /// <summary>Começa ou continua. <paramref name="sources"/> dá os endereços de quem mandou (tentados em ordem).</summary>
    public Task StartAsync(Func<IReadOnlyList<IPEndPoint>> sources)
    {
        lock (_gate)
        {
            if (IsRunning) return _task!;
            if (State is FileDownloadState.Completed or FileDownloadState.Canceled) return Task.CompletedTask;
            _cancelRequested = false;
            _run = new CancellationTokenSource();
            var token = _run.Token;
            Error = null;
            SetState(FileDownloadState.Connecting);
            _task = Task.Run(() => RunAsync(sources, token), CancellationToken.None);
            return _task;
        }
    }

    /// <summary>Espera a vez (já tem downloads demais acontecendo): fica "na fila" até alguém chamar <see cref="StartAsync"/>.</summary>
    public void Queue()
    {
        lock (_gate)
        {
            if (IsRunning || State is FileDownloadState.Completed or FileDownloadState.Canceled) return;
            Error = null;
        }
        SetState(FileDownloadState.Waiting);
    }

    /// <summary>Para e guarda onde parou (Continuar segue daqui).</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            _run?.Cancel();
        }
    }

    /// <summary>Para e apaga o que já tinha baixado.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cancelRequested = true;
            if (IsRunning)
            {
                _run?.Cancel();
                return;
            }
        }
        DeletePartial();
        Received = 0;
        SetState(FileDownloadState.Canceled);
    }

    /// <summary>Espera terminar (nos testes e ao fechar o ScreenFlow).</summary>
    public Task WaitAsync() => _task ?? Task.CompletedTask;

    private async Task RunAsync(Func<IReadOnlyList<IPEndPoint>> sources, CancellationToken token)
    {
        FileStream? part = null;
        IncrementalHash? hash = null;
        try
        {
            var folder = Path.GetDirectoryName(Destination);
            if (folder is null || !Directory.Exists(folder))
            {
                Fail("A pasta escolhida não existe mais. Baixe de novo e escolha outra.");
                return;
            }
            (part, hash, var written) = await OpenPartAsync(token).ConfigureAwait(false);
            Received = written;
            if (written < File.Size && !HasRoom(File.Size - written, out var problem))
            {
                Fail(problem!);
                return;
            }
            var attempts = 0;
            string? lastProblem = null;
            while (written < File.Size)
            {
                token.ThrowIfCancellationRequested();
                var before = written;
                try
                {
                    written = await DownloadOnceAsync(sources(), part, hash, written, token).ConfigureAwait(false);
                }
                catch (FileTransferRefusedException refused)
                {
                    Checkpoint(part, written);
                    if (refused.Status == FileTransferStatus.Busy) lastProblem = refused.Message;
                    else
                    {
                        Fail(refused.Message);
                        return;
                    }
                }
                catch (Exception exception) when (!token.IsCancellationRequested && exception is IOException or SocketException or TimeoutException or OperationCanceledException)
                {
                    lastProblem = exception is FileTransferUnreachableException ? exception.Message : "A conexão com o PC de quem mandou caiu.";
                    if (IsDiskFull(exception))
                    {
                        Checkpoint(part, written);
                        Fail("O disco encheu. Libere espaço e clique em Continuar.");
                        return;
                    }
                }
                Checkpoint(part, written);
                if (written >= File.Size) break;
                // Avançou alguma coisa: a rede está funcionando, então as tentativas recomeçam.
                attempts = written > before ? 1 : attempts + 1;
                if (attempts >= MaxAttempts)
                {
                    Pause(lastProblem ?? "Não deu para continuar o download.");
                    return;
                }
                SetState(FileDownloadState.Connecting);
                await Task.Delay(RetryDelay(attempts), token).ConfigureAwait(false);
            }
            SetState(FileDownloadState.Verifying);
            await part.FlushAsync(token).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            await part.DisposeAsync().ConfigureAwait(false);
            part = null;
            if (actual != File.Hash)
            {
                DeletePartial();
                Received = 0;
                Fail("O arquivo chegou diferente do original (a conferência SHA-256 não bateu) e foi apagado. Tente baixar de novo.");
                return;
            }
            try { System.IO.File.Move(PartPath, Destination, overwrite: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SaveCheckpoint(File.Size);
                Pause($"O arquivo chegou certo, mas não deu para dar o nome final ({exception.Message}). Feche o que estiver usando \"{Path.GetFileName(Destination)}\" e clique em Continuar.");
                return;
            }
            MarkFromInternet(Destination);
            DeleteCheckpoint();
            Received = File.Size;
            SetState(FileDownloadState.Completed);
            Log.Info($"Arquivo do chat baixado e conferido: {Path.GetFileName(Destination)} ({FileNames.SizeText(File.Size)}).");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (part is not null)
            {
                try { Checkpoint(part, Received); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
                await part.DisposeAsync().ConfigureAwait(false);
                part = null;
            }
            if (_cancelRequested)
            {
                DeletePartial();
                Received = 0;
                SetState(FileDownloadState.Canceled);
            }
            else
            {
                BytesPerSecond = 0;
                SetState(FileDownloadState.Paused);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Download do chat parou: {exception.Message}");
            Fail(IsDiskFull(exception) ? "O disco encheu. Libere espaço e clique em Continuar." : $"Não deu para gravar o arquivo: {exception.Message}");
        }
        finally
        {
            if (part is not null) await part.DisposeAsync().ConfigureAwait(false);
            hash?.Dispose();
        }
    }

    /// <summary>
    /// Abre o .sfpart. Se já tinha um (com o ponto onde parou guardado), confere o que já estava lá refazendo o SHA-256
    /// daquele começo e continua do fim dele; senão começa do zero.
    /// </summary>
    private async Task<(FileStream Part, IncrementalHash Hash, long Written)> OpenPartAsync(CancellationToken token)
    {
        var confirmed = 0L;
        var checkpoint = ReadCheckpoint();
        if (checkpoint is not null && checkpoint.File.Hash == File.Hash && checkpoint.File.Size == File.Size
            && string.Equals(checkpoint.Destination, Destination, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(PartPath))
        {
            confirmed = checkpoint.Confirmed;
        }
        var part = new FileStream(PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.Asynchronous);
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var start = Math.Min(confirmed, part.Length);
            start -= start % FileTransferProtocol.ChunkBytes;
            if (start != File.Size && start > File.Size) start = 0;
            part.SetLength(start);
            if (start > 0)
            {
                SetState(FileDownloadState.Verifying);
                part.Position = 0;
                await FileHasher.AppendAsync(hash, part, start, null, token).ConfigureAwait(false);
            }
            part.Position = start;
            return (part, hash, start);
        }
        catch
        {
            await part.DisposeAsync().ConfigureAwait(false);
            hash.Dispose();
            throw;
        }
    }

    /// <summary>Uma conexão: pede a partir de <paramref name="written"/> e grava até acabar ou cair. Devolve até onde chegou.</summary>
    private async Task<long> DownloadOnceAsync(IReadOnlyList<IPEndPoint> sources, FileStream part, IncrementalHash hash, long written, CancellationToken token)
    {
        SetState(FileDownloadState.Connecting);
        using var client = await ConnectAsync(sources, token).ConfigureAwait(false);
        client.NoDelay = true;
        client.ReceiveBufferSize = 1024 * 1024;
        var stream = client.GetStream();
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(token);
        watchdog.CancelAfter(StallTimeout);
        var share = File.ShareBytes();
        var key = File.KeyBytes();
        var clientRandom = RandomNumberGenerator.GetBytes(FileTransferProtocol.RandomBytes);
        await stream.WriteAsync(FileTransferProtocol.Request(share, written, clientRandom, key), watchdog.Token).ConfigureAwait(false);
        var header = new byte[FileTransferProtocol.ResponseBytes];
        await stream.ReadExactlyAsync(header, watchdog.Token).ConfigureAwait(false);
        if (!FileTransferProtocol.TryReadResponse(header, out var status, out var size, out var serverRandom))
            throw new IOException("Resposta estranha do outro PC.");
        if (status != FileTransferStatus.Ok) throw new FileTransferRefusedException(status);
        if (size != File.Size) throw new FileTransferRefusedException(FileTransferStatus.Changed);
        var sessionKey = FileTransferProtocol.SessionKey(key, share, clientRandom, serverRandom);
        using var aes = new AesGcm(sessionKey, FileTransferProtocol.TagBytes);
        var cipher = ArrayPool<byte>.Shared.Rent(FileTransferProtocol.ChunkBytes + FileTransferProtocol.TagBytes);
        var plain = ArrayPool<byte>.Shared.Rent(FileTransferProtocol.ChunkBytes);
        var lengthBytes = new byte[FileTransferProtocol.FrameHeaderBytes];
        var nonce = new byte[FileTransferProtocol.NonceBytes];
        SetState(FileDownloadState.Downloading);
        var lastCheckpoint = (Bytes: written, Time: DateTime.UtcNow);
        var speedMark = (Bytes: written, Time: DateTime.UtcNow);
        try
        {
            var count = FileTransferProtocol.ChunkCount(File.Size);
            for (var index = written / FileTransferProtocol.ChunkBytes; index < count; index++)
            {
                var expected = FileTransferProtocol.ChunkLength(File.Size, index);
                await stream.ReadExactlyAsync(lengthBytes, watchdog.Token).ConfigureAwait(false);
                // O tamanho do bloco já é sabido: qualquer outro número é o outro lado errado (e nunca vira memória gasta).
                if (BinaryPrimitives.ReadInt32BigEndian(lengthBytes) != expected) throw new IOException("Bloco de tamanho errado.");
                await stream.ReadExactlyAsync(cipher.AsMemory(0, expected + FileTransferProtocol.TagBytes), watchdog.Token).ConfigureAwait(false);
                watchdog.CancelAfter(StallTimeout);
                FileTransferProtocol.WriteNonce(index, nonce);
                try
                {
                    aes.Decrypt(nonce, cipher.AsSpan(0, expected), cipher.AsSpan(expected, FileTransferProtocol.TagBytes), plain.AsSpan(0, expected));
                }
                catch (CryptographicException)
                {
                    throw new IOException("Um bloco chegou mexido.");
                }
                await part.WriteAsync(plain.AsMemory(0, expected), token).ConfigureAwait(false);
                hash.AppendData(plain, 0, expected);
                written += expected;
                Received = written;
                var now = DateTime.UtcNow;
                var elapsed = (now - speedMark.Time).TotalSeconds;
                if (elapsed >= 0.5)
                {
                    var instant = (written - speedMark.Bytes) / elapsed;
                    BytesPerSecond = BytesPerSecond <= 0 ? instant : BytesPerSecond * 0.7 + instant * 0.3;
                    speedMark = (written, now);
                }
                if (written - lastCheckpoint.Bytes >= CheckpointBytes || now - lastCheckpoint.Time >= CheckpointEvery)
                {
                    Checkpoint(part, written);
                    lastCheckpoint = (written, now);
                }
                RaiseProgress();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("O outro PC parou de mandar.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cipher);
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
        }
        return written;
    }

    private static async Task<TcpClient> ConnectAsync(IReadOnlyList<IPEndPoint> sources, CancellationToken token)
    {
        if (sources.Count == 0) throw new FileTransferUnreachableException("Quem mandou não está em nenhuma sala ou servidor agora.");
        foreach (var source in sources.Distinct())
        {
            var client = new TcpClient(source.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await client.ConnectAsync(source, timeout.Token).ConfigureAwait(false);
                return client;
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException && !token.IsCancellationRequested)
            {
                client.Dispose();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        throw new FileTransferUnreachableException("Não deu para falar com o PC de quem mandou (ele pode ter fechado o ScreenFlow, ou o firewall bloqueia a porta TCP de arquivos).");
    }

    private bool HasRoom(long needed, out string? problem)
    {
        problem = null;
        try
        {
            var root = Path.GetPathRoot(Destination);
            if (string.IsNullOrEmpty(root)) return true;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free >= needed + 16 * 1024 * 1024) return true;
            problem = $"Falta espaço no disco: o arquivo precisa de {FileNames.SizeText(needed)} e tem {FileNames.SizeText(free)} livres.";
            return false;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsDiskFull(Exception exception) => exception is IOException io && (io.HResult & 0xFFFF) is 0x70 or 0x27;

    private void Checkpoint(FileStream part, long written)
    {
        part.Flush(flushToDisk: true);
        SaveCheckpoint(written);
    }

    internal void SaveCheckpoint(long confirmed)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var checkpoint = new FileDownloadCheckpoint(File, Destination, confirmed, DateTimeOffset.UtcNow);
            new EncryptedLog(_statePath, _storeKey).Rewrite([JsonSerializer.SerializeToUtf8Bytes(checkpoint, Options)]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"O ponto do download não foi guardado: {exception.Message}");
        }
    }

    private FileDownloadCheckpoint? ReadCheckpoint()
    {
        try
        {
            if (!System.IO.File.Exists(_statePath)) return null;
            var records = new EncryptedLog(_statePath, _storeKey).ReadAll(1);
            return records.Count == 0 ? null : JsonSerializer.Deserialize<FileDownloadCheckpoint>(records[0], Options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void DeleteCheckpoint()
    {
        try { System.IO.File.Delete(_statePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void DeletePartial()
    {
        try { System.IO.File.Delete(PartPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"O pedaço baixado não foi apagado: {exception.Message}");
        }
        DeleteCheckpoint();
    }

    /// <summary>A marca de "veio da internet" (Zone.Identifier): o Windows avisa antes de rodar um programa baixado.</summary>
    internal static void MarkFromInternet(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { System.IO.File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Pen drives em FAT32 e exFAT não guardam a marca.
        }
    }

    private void Fail(string message)
    {
        Error = message;
        BytesPerSecond = 0;
        SetState(FileDownloadState.Failed);
    }

    private void Pause(string message)
    {
        Error = message;
        BytesPerSecond = 0;
        SetState(FileDownloadState.Paused);
    }

    private void SetState(FileDownloadState state)
    {
        State = state;
        _lastChanged = DateTime.UtcNow;
        Changed?.Invoke(this);
    }

    private void RaiseProgress()
    {
        var now = DateTime.UtcNow;
        if (now - _lastChanged < ChangedEvery) return;
        _lastChanged = now;
        Changed?.Invoke(this);
    }
}

/// <summary>Quem tem o arquivo respondeu que não dá (não tem mais, mudou, ocupado).</summary>
public sealed class FileTransferRefusedException(FileTransferStatus status) : Exception(Describe(status))
{
    public FileTransferStatus Status { get; } = status;

    public static string Describe(FileTransferStatus status) => status switch
    {
        FileTransferStatus.NotFound => "Quem mandou não compartilha mais este arquivo (parou de compartilhar, ou passaram 30 dias).",
        FileTransferStatus.Changed => "O arquivo mudou ou foi apagado no PC de quem mandou. Peça para mandar de novo.",
        FileTransferStatus.Busy => "Muita gente baixando deste PC agora. Tentando de novo…",
        _ => "O outro PC não entendeu o pedido (versões diferentes do ScreenFlow?).",
    };
}

/// <summary>Nenhum endereço de quem mandou respondeu.</summary>
public sealed class FileTransferUnreachableException(string message) : IOException(message);
