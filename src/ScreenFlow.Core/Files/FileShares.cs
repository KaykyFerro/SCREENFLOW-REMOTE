using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Security;

namespace ScreenFlow.Core.Files;

/// <summary>
/// Um arquivo que este PC mandou no chat: onde ele está, como ele estava quando foi mandado (tamanho, data e SHA-256) e a
/// chave do anexo. Quem pede recebe o arquivo de onde ele está (nada é copiado); se ele mudar ou sumir, o download para
/// com o aviso de que o arquivo mudou.
/// </summary>
public sealed record FileShareEntry(
    string Share,
    string Path,
    string Name,
    long Size,
    long WriteTicks,
    string Hash,
    string Key,
    DateTimeOffset Created,
    Guid? Peer = null)
{
    public byte[] KeyBytes() => Convert.FromBase64String(Key);

    /// <summary>O anexo da mensagem, com a porta e os IPs deste PC.</summary>
    public ChatFile ToChatFile(int port, ChatFileKind kind, int duration = 0, IEnumerable<string>? addresses = null)
    {
        var list = addresses?.Take(ChatFile.MaxAddresses).ToArray();
        return new ChatFile(Share, Name, Size, Hash, Key, port, kind, duration, list is { Length: > 0 } ? list : null);
    }
}

/// <summary>
/// (1.5.0) Os arquivos que este PC mandou no chat (o que o <see cref="FileTransferServer"/> pode entregar), guardados
/// cifrados (as chaves dos anexos vão aqui). Cada um vale por <see cref="Lifetime"/>; depois disso, ou se você parar de
/// compartilhar, quem pedir ouve que não tem mais.
/// </summary>
public sealed class FileShareRegistry
{
    public const int MaxShares = 2000;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly EncryptedLog? _log;
    private readonly Dictionary<string, FileShareEntry> _shares = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;

    /// <param name="path">Onde guardar (null: só na memória, nos testes e na demonstração).</param>
    public FileShareRegistry(string? path, byte[]? key, Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        if (path is not null && key is { Length: 32 })
        {
            _log = new EncryptedLog(path, key);
            Load();
        }
    }

    public int Count
    {
        get { lock (_gate) return _shares.Count; }
    }

    /// <summary>Passa a compartilhar um arquivo (já com o SHA-256). Devolve o registro com o id e a chave novos.</summary>
    public FileShareEntry Add(string path, byte[] hash, long size, DateTime lastWriteUtc, Guid? peer = null, string? name = null)
    {
        if (hash.Length != FileTransferProtocol.HashBytes) throw new ArgumentException("Hash inválido.", nameof(hash));
        if (size is <= 0 or > FileTransferProtocol.MaxFileBytes) throw new ArgumentOutOfRangeException(nameof(size));
        var entry = new FileShareEntry(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(FileTransferProtocol.ShareBytes)).ToLowerInvariant(),
            Path.GetFullPath(path),
            FileNames.Clean(name ?? Path.GetFileName(path)),
            size,
            lastWriteUtc.ToUniversalTime().Ticks,
            Convert.ToHexString(hash).ToLowerInvariant(),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(FileTransferProtocol.KeyBytes)),
            _now(),
            peer);
        lock (_gate)
        {
            _shares[entry.Share] = entry;
            PruneLocked();
        }
        Save();
        return entry;
    }

    public FileShareEntry? Find(string share)
    {
        lock (_gate)
        {
            if (!_shares.TryGetValue(share, out var entry)) return null;
            if (_now() - entry.Created <= Lifetime) return entry;
        }
        Remove(share);
        return null;
    }

    /// <summary>Para de compartilhar (quem pedir depois ouve que não tem mais).</summary>
    public bool Remove(string share)
    {
        bool removed;
        lock (_gate) removed = _shares.Remove(share);
        if (removed) Save();
        return removed;
    }

    private void PruneLocked()
    {
        var now = _now();
        foreach (var old in _shares.Values.Where(e => now - e.Created > Lifetime).Select(e => e.Share).ToList()) _shares.Remove(old);
        if (_shares.Count <= MaxShares) return;
        foreach (var old in _shares.Values.OrderBy(e => e.Created).Take(_shares.Count - MaxShares).Select(e => e.Share).ToList()) _shares.Remove(old);
    }

    private void Save()
    {
        if (_log is null) return;
        List<FileShareEntry> list;
        lock (_gate) list = _shares.Values.ToList();
        try { _log.Rewrite([JsonSerializer.SerializeToUtf8Bytes(list, Options)]); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"A lista de arquivos mandados não foi gravada: {exception.Message}");
        }
    }

    private void Load()
    {
        try
        {
            var records = _log!.ReadAll(1);
            if (records.Count == 0) return;
            var list = JsonSerializer.Deserialize<List<FileShareEntry>>(records[0], Options) ?? [];
            lock (_gate)
            {
                foreach (var entry in list)
                {
                    if (!ChatFiles.IsHex(entry.Share, FileTransferProtocol.ShareBytes) || !ChatFiles.IsHex(entry.Hash, FileTransferProtocol.HashBytes)
                        || !ChatFiles.IsKey(entry.Key) || string.IsNullOrEmpty(entry.Path) || entry.Size <= 0) continue;
                    _shares[entry.Share] = entry with { Name = FileNames.Clean(entry.Name) };
                }
                PruneLocked();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"A lista de arquivos mandados não abriu: {exception.Message}");
        }
    }
}

/// <summary>SHA-256 de um arquivo inteiro, lendo aos pedaços (nunca o arquivo inteiro na memória).</summary>
public static class FileHasher
{
    private const int BufferBytes = 1024 * 1024;

    public static async Task<byte[]> HashAsync(string path, IProgress<double>? progress = null, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await AppendAsync(hash, stream, stream.Length, progress, token).ConfigureAwait(false);
        return hash.GetHashAndReset();
    }

    /// <summary>Soma ao hash os próximos <paramref name="count"/> bytes de <paramref name="stream"/>.</summary>
    internal static async Task AppendAsync(IncrementalHash hash, Stream stream, long count, IProgress<double>? progress, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            long done = 0;
            var lastReport = 0L;
            while (done < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferBytes, count - done)), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("O arquivo acabou antes do esperado.");
                hash.AppendData(buffer, 0, read);
                done += read;
                if (progress is not null && (done - lastReport >= 16 * BufferBytes || done == count))
                {
                    lastReport = done;
                    progress.Report(count == 0 ? 1 : done / (double)count);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

/// <summary>Um arquivo do chat que este PC baixou (e conferiu): onde ele ficou.</summary>
public sealed record FileReceipt(string Share, string Path, DateTimeOffset Completed);

/// <summary>
/// (1.5.0) Os arquivos do chat que este PC já baixou, para o cartão da mensagem mostrar "Abrir" e "Mostrar na pasta"
/// depois que o ScreenFlow abre de novo. Cifrado (os nomes e as pastas são da sua conta).
/// </summary>
public sealed class FileReceipts
{
    public const int MaxReceipts = 2000;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly EncryptedLog? _log;
    private readonly Dictionary<string, FileReceipt> _receipts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public FileReceipts(string? path, byte[]? key)
    {
        if (path is null || key is not { Length: 32 }) return;
        _log = new EncryptedLog(path, key);
        try
        {
            var records = _log.ReadAll(1);
            if (records.Count == 0) return;
            foreach (var receipt in JsonSerializer.Deserialize<List<FileReceipt>>(records[0], Options) ?? [])
            {
                if (ChatFiles.IsHex(receipt.Share, FileTransferProtocol.ShareBytes) && !string.IsNullOrWhiteSpace(receipt.Path)) _receipts[receipt.Share] = receipt;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"A lista de arquivos baixados não abriu: {exception.Message}");
        }
    }

    public FileReceipt? Find(string share)
    {
        lock (_gate) return _receipts.GetValueOrDefault(share);
    }

    public void Add(string share, string path, DateTimeOffset now)
    {
        lock (_gate)
        {
            _receipts[share] = new FileReceipt(share, path, now);
            if (_receipts.Count > MaxReceipts)
            {
                foreach (var old in _receipts.Values.OrderBy(r => r.Completed).Take(_receipts.Count - MaxReceipts).Select(r => r.Share).ToList()) _receipts.Remove(old);
            }
        }
        Save();
    }

    public void Remove(string share)
    {
        bool removed;
        lock (_gate) removed = _receipts.Remove(share);
        if (removed) Save();
    }

    private void Save()
    {
        if (_log is null) return;
        List<FileReceipt> list;
        lock (_gate) list = _receipts.Values.ToList();
        try { _log.Rewrite([JsonSerializer.SerializeToUtf8Bytes(list, Options)]); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"A lista de arquivos baixados não foi gravada: {exception.Message}");
        }
    }
}
