using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.Core.Room;

/// <summary>
/// Os arquivos da sala guardados neste PC (a sua foto, as fotos de quem está na rede e as imagens do chat): um arquivo
/// por id, numa pasta. Pode ser chamado de várias threads ao mesmo tempo (a rede lê daqui para mandar a quem pede).
/// </summary>
public sealed class RoomBlobStore
{
    /// <summary>Quanto fica na memória (o resto é lido do disco quando alguém pede).</summary>
    public const long MemoryBytes = 24L * 1024 * 1024;
    public const long MaxDiskBytes = 300L * 1024 * 1024;
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _memory = [];
    private readonly LinkedList<string> _recent = new();
    private long _memoryBytes;

    public RoomBlobStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    private string PathOf(string id) => Path.Combine(Directory, id + ".blob");

    public bool Has(string id)
    {
        if (!RoomBlob.IsValidId(id)) return false;
        lock (_gate)
            if (_memory.ContainsKey(id)) return true;
        return File.Exists(PathOf(id));
    }

    /// <summary>O tamanho do arquivo, ou null se ele não está aqui.</summary>
    public int? SizeOf(string id)
    {
        if (!RoomBlob.IsValidId(id)) return null;
        lock (_gate)
            if (_memory.TryGetValue(id, out var data)) return data.Length;
        try
        {
            var info = new FileInfo(PathOf(id));
            return info.Exists && info.Length <= RoomBlob.MaxImageBytes ? (int)info.Length : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>O conteúdo, ou null se não está aqui (ou foi estragado no disco).</summary>
    public byte[]? Get(string id)
    {
        if (!RoomBlob.IsValidId(id)) return null;
        lock (_gate)
            if (_memory.TryGetValue(id, out var cached))
            {
                Touch(id);
                return cached;
            }
        byte[] data;
        try
        {
            var path = PathOf(id);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > RoomBlob.MaxImageBytes) return null;
            data = File.ReadAllBytes(path);
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { } // usado agora: não sai na limpeza
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        if (RoomBlob.IdOf(data) != id)
        {
            Log.Warn($"Arquivo da sala {id} estragado no disco: apagando.");
            Delete(id);
            return null;
        }
        Remember(id, data);
        return data;
    }

    /// <summary>Guarda um arquivo novo deste PC (a sua foto, uma imagem que você mandou) e devolve o id dele.</summary>
    public string Put(byte[] data)
    {
        if (data.Length is 0 or > RoomBlob.MaxImageBytes) throw new ArgumentException("Arquivo vazio ou grande demais.", nameof(data));
        var id = RoomBlob.IdOf(data);
        Write(id, data);
        return id;
    }

    /// <summary>Guarda um arquivo que chegou da rede. false se o conteúdo não bate com o id.</summary>
    public bool Save(string id, byte[] data)
    {
        if (!RoomBlob.IsValidId(id) || data.Length is 0 or > RoomBlob.MaxImageBytes || RoomBlob.IdOf(data) != id) return false;
        Write(id, data);
        return true;
    }

    public void Delete(string id)
    {
        if (!RoomBlob.IsValidId(id)) return;
        lock (_gate) Forget(id);
        try { File.Delete(PathOf(id)); } catch { }
    }

    /// <summary>
    /// Apaga o que não é usado há <see cref="KeepFor"/> e, se a pasta passar de <see cref="MaxDiskBytes"/>, os mais
    /// antigos. Os de <paramref name="keep"/> (a sua foto) ficam sempre.
    /// </summary>
    public int Trim(IEnumerable<string?> keep, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var kept = keep.Where(k => k is not null).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        try
        {
            var files = new DirectoryInfo(Directory).GetFiles("*.blob")
                .Where(f => !kept.Contains(Path.GetFileNameWithoutExtension(f.Name)))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (now - file.LastWriteTimeUtc <= KeepFor && total <= MaxDiskBytes) break;
                total -= file.Length;
                Delete(Path.GetFileNameWithoutExtension(file.Name));
                removed++;
            }
            // Restos de uma gravação interrompida.
            foreach (var temp in new DirectoryInfo(Directory).GetFiles("*.tmp"))
                if (now - temp.LastWriteTimeUtc > TimeSpan.FromHours(1))
                    try { temp.Delete(); } catch { }
        }
        catch (Exception exception) { Log.Debug($"Limpeza dos arquivos da sala: {exception.Message}"); }
        return removed;
    }

    private void Write(string id, byte[] data)
    {
        Remember(id, data);
        var path = PathOf(id);
        if (File.Exists(path))
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
            return;
        }
        var temp = Path.Combine(Directory, $"{id}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception)
        {
            // Fica só na memória enquanto o ScreenFlow estiver aberto.
            Log.Warn($"Arquivo da sala {id} não foi gravado: {exception.Message}");
            try { File.Delete(temp); } catch { }
        }
    }

    private void Remember(string id, byte[] data)
    {
        lock (_gate)
        {
            if (_memory.ContainsKey(id))
            {
                Touch(id);
                return;
            }
            _memory[id] = data;
            _recent.AddFirst(id);
            _memoryBytes += data.Length;
            while (_memoryBytes > MemoryBytes && _recent.Last is { } oldest && oldest.Value != id) Forget(oldest.Value);
        }
    }

    private void Touch(string id)
    {
        var node = _recent.Find(id);
        if (node is null || node == _recent.First) return;
        _recent.Remove(node);
        _recent.AddFirst(node);
    }

    private void Forget(string id)
    {
        if (!_memory.Remove(id, out var data)) return;
        _memoryBytes -= data.Length;
        _recent.Remove(id);
    }
}
