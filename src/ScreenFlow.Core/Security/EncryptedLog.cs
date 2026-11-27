using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.Core.Security;

/// <summary>
/// (1.5.0) Um arquivo de registros cifrados (AES-256-GCM), só acrescentando no fim: o histórico de um servidor ou de uma
/// conversa privada guardado neste PC. Cada registro vai com o seu nonce e a sua etiqueta, então um registro cortado (o
/// PC desligou no meio) só perde ele mesmo. A chave fica nas configurações, cifrada pelo Windows.
/// </summary>
public sealed class EncryptedLog
{
    private static readonly byte[] Magic = "SFL1"u8.ToArray();
    public const int MaxRecordBytes = 256 * 1024;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly string _path;
    private readonly byte[] _key;
    private readonly object _gate = new();

    public EncryptedLog(string path, byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("A chave do histórico precisa ter 32 bytes.", nameof(key));
        _path = path;
        _key = key.ToArray();
    }

    public string FilePath => _path;

    public long Length
    {
        get
        {
            try { return File.Exists(_path) ? new FileInfo(_path).Length : 0; }
            catch (IOException) { return 0; }
        }
    }

    /// <summary>Acrescenta um registro no fim do arquivo.</summary>
    public void Append(ReadOnlySpan<byte> record)
    {
        if (record.Length is 0 or > MaxRecordBytes) throw new ArgumentOutOfRangeException(nameof(record));
        var sealedRecord = Seal(record);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length == 0) stream.Write(Magic);
            else if (!HasMagic(stream)) throw new InvalidDataException("O arquivo do histórico não é do ScreenFlow.");
            stream.Seek(0, SeekOrigin.End);
            stream.Write(sealedRecord);
            stream.Flush(flushToDisk: false);
        }
    }

    /// <summary>
    /// Lê os registros que abrem com a chave, do mais antigo para o mais novo (só os últimos <paramref name="max"/>). Um
    /// pedaço quebrado no fim é ignorado; um registro que não abre (mexido) é pulado.
    /// </summary>
    public List<byte[]> ReadAll(int max = int.MaxValue)
    {
        var records = new Queue<byte[]>();
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!HasMagic(stream)) return [];
            Span<byte> header = stackalloc byte[4];
            while (true)
            {
                if (stream.Read(header) != 4) break;
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length is <= NonceBytes + TagBytes or > MaxRecordBytes + NonceBytes + TagBytes) break;
                var data = new byte[length];
                if (stream.ReadAtLeast(data, length, throwOnEndOfStream: false) != length) break;
                if (Open(data) is not { } plain) continue;
                records.Enqueue(plain);
                if (records.Count > max) records.Dequeue();
            }
        }
        return records.ToList();
    }

    /// <summary>Reescreve o arquivo só com estes registros (para ele não crescer para sempre).</summary>
    public void Rewrite(IEnumerable<byte[]> records)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Magic);
                foreach (var record in records)
                    if (record.Length is > 0 and <= MaxRecordBytes) stream.Write(Seal(record));
            }
            File.Move(temp, _path, overwrite: true);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (IOException exception) { Log.Warn($"O histórico não foi apagado: {exception.Message}"); }
        }
    }

    private static bool HasMagic(FileStream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        stream.Seek(0, SeekOrigin.Begin);
        return stream.Read(magic) == 4 && magic.SequenceEqual(Magic);
    }

    private byte[] Seal(ReadOnlySpan<byte> plain)
    {
        var output = new byte[4 + NonceBytes + plain.Length + TagBytes];
        BinaryPrimitives.WriteInt32BigEndian(output, NonceBytes + plain.Length + TagBytes);
        var nonce = output.AsSpan(4, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plain, output.AsSpan(4 + NonceBytes, plain.Length), output.AsSpan(output.Length - TagBytes), Magic);
        return output;
    }

    private byte[]? Open(ReadOnlySpan<byte> data)
    {
        var plain = new byte[data.Length - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(data[..NonceBytes], data.Slice(NonceBytes, plain.Length), data[^TagBytes..], plain, Magic);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }
}

/// <summary>
/// (1.5.0) Um histórico de mensagens (de qualquer tipo que vire JSON) num <see cref="EncryptedLog"/>: abre as últimas,
/// acrescenta uma por vez e, quando o arquivo passa do limite, fica só com as mais novas.
/// </summary>
public sealed class EncryptedHistory<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly EncryptedLog _log;
    private readonly int _keep;
    private readonly long _maxBytes;
    private int _appended;

    public EncryptedHistory(string path, byte[] key, int keep = 2000, long maxBytes = 8 * 1024 * 1024)
    {
        _log = new EncryptedLog(path, key);
        _keep = keep;
        _maxBytes = maxBytes;
    }

    public string FilePath => _log.FilePath;

    /// <summary>As últimas <paramref name="max"/> mensagens guardadas (as que não abrem ou não leem ficam de fora).</summary>
    public List<T> Load(int max)
    {
        var list = new List<T>();
        try
        {
            foreach (var record in _log.ReadAll(max))
            {
                try
                {
                    if (JsonSerializer.Deserialize<T>(record, Options) is { } item) list.Add(item);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"O histórico {Path.GetFileName(_log.FilePath)} não abriu: {exception.Message}");
        }
        return list;
    }

    public void Append(T item)
    {
        try
        {
            _log.Append(JsonSerializer.SerializeToUtf8Bytes(item, Options));
            if (++_appended % 50 == 0 && _log.Length > _maxBytes) Compact();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn($"Mensagem não guardada no histórico: {exception.Message}");
        }
    }

    /// <summary>Fica só com as <c>keep</c> mais novas.</summary>
    public void Compact()
    {
        var records = _log.ReadAll(_keep);
        _log.Rewrite(records);
        Log.Info($"Histórico {Path.GetFileName(_log.FilePath)} compactado ({records.Count} mensagens).");
    }

    public void Delete() => _log.Delete();
}
