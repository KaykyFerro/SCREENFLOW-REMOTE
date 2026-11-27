using System.Text.Json;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;

namespace ScreenFlow.Core.Server;

/// <summary>
/// Os canais de um servidor ScreenFlow (1.4.1), guardados num arquivo: qualquer pessoa no servidor cria, renomeia e apaga,
/// e eles continuam lá quando o servidor reinicia. O chat "geral" e a voz "Geral" existem sempre e não mudam (as versões
/// antigas só conhecem eles). Um canal de texto renomeado leva a conversa junto: o servidor lembra o nome antigo e manda
/// o histórico dele com o nome novo.
/// </summary>
public sealed class ChannelStore
{
    private const int MaxRenames = 64;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string? _file;
    private readonly string? _renamesFile;
    private readonly object _gate = new();
    private List<RoomChannel> _channels = [];
    // Nome antigo de um canal de texto (a chave) → o nome de agora, na ordem em que foram renomeados.
    private List<KeyValuePair<string, string>> _renames = [];

    public ChannelStore(string? file)
    {
        _file = file;
        _renamesFile = file is null ? null : Path.Combine(Path.GetDirectoryName(file) ?? ".", Path.GetFileNameWithoutExtension(file) + "-renomeados.json");
        Load();
    }

    public IReadOnlyList<RoomChannel> All
    {
        get { lock (_gate) return _channels.ToArray(); }
    }

    /// <summary>
    /// O motivo de uma mudança não valer nesta lista de canais (null: vale). O app usa antes de mandar a mudança para o
    /// servidor, para avisar na hora em vez de esperar a lista não mudar.
    /// </summary>
    public static string? Problem(IReadOnlyList<RoomChannel> channels, RoomChannelOperation operation, RoomChannel channel, RoomChannel? renamed = null)
    {
        var index = IndexOf(channels, channel);
        switch (operation)
        {
            case RoomChannelOperation.Create:
                if (channel.IsDefault || index >= 0) return "Esse canal já existe.";
                if (channels.Count >= RoomChannels.MaxPerServer) return $"O servidor já tem {RoomChannels.MaxPerServer} canais. Apague um para criar outro.";
                return null;
            case RoomChannelOperation.Rename:
                if (renamed is null || channel.IsDefault || renamed.IsDefault) return "O chat geral e a voz Geral não mudam de nome.";
                if (index < 0) return "Esse canal não existe mais.";
                if (renamed.Kind != channel.Kind) return "Um canal não muda de tipo.";
                var clash = IndexOf(channels, renamed);
                if (clash >= 0 && clash != index) return "Já existe um canal com esse nome.";
                return null;
            case RoomChannelOperation.Delete:
                if (channel.IsDefault) return "O chat geral e a voz Geral não podem ser apagados.";
                return index < 0 ? "Esse canal não existe mais." : null;
            default:
                return "Operação desconhecida.";
        }
    }

    private static int IndexOf(IReadOnlyList<RoomChannel> channels, RoomChannel channel)
    {
        for (var i = 0; i < channels.Count; i++)
            if (channels[i].Same(channel)) return i;
        return -1;
    }

    /// <summary>Muda a lista. Retorna null se deu certo, ou o motivo de não ter mudado.</summary>
    public string? Apply(RoomChannelOperation operation, RoomChannel channel, RoomChannel? renamed = null)
    {
        lock (_gate)
        {
            if (Problem(_channels, operation, channel, renamed) is { } problem) return problem;
            var index = IndexOf(_channels, channel);
            switch (operation)
            {
                case RoomChannelOperation.Create:
                    _channels.Add(channel);
                    // Um canal novo com o nome antigo de outro não herda a conversa de lá.
                    if (channel.Kind == RoomChannelKind.Text) _renames.RemoveAll(r => r.Key == channel.Key);
                    break;
                case RoomChannelOperation.Rename:
                    var old = _channels[index];
                    _channels[index] = renamed!;
                    if (old.Kind == RoomChannelKind.Text) Remember(old, renamed!);
                    break;
                case RoomChannelOperation.Delete:
                    _channels.RemoveAt(index);
                    break;
            }
            Save();
            return null;
        }
    }

    /// <summary>O nome de agora de um canal de texto que pode ter sido renomeado (o próprio nome, se não foi).</summary>
    public string? CurrentTextName(string? channel)
    {
        if (channel is null || RoomChannels.IsDefaultText(channel)) return channel;
        var key = RoomChannels.Key(channel);
        lock (_gate)
        {
            foreach (var (from, to) in _renames)
                if (from == key) return to;
        }
        return channel;
    }

    private void Remember(RoomChannel old, RoomChannel renamed)
    {
        // Quem apontava para o nome antigo passa a apontar para o novo (renomear duas vezes leva a conversa junto).
        for (var i = 0; i < _renames.Count; i++)
            if (RoomChannels.SameName(_renames[i].Value, old.Name)) _renames[i] = new(_renames[i].Key, renamed.Name);
        _renames.RemoveAll(r => r.Key == old.Key || r.Key == renamed.Key);
        _renames.Add(new(old.Key, renamed.Name));
        if (_renames.Count > MaxRenames) _renames.RemoveRange(0, _renames.Count - MaxRenames);
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file)) return;
        try
        {
            var read = JsonSerializer.Deserialize<List<RoomChannel>>(File.ReadAllText(_file), Json) ?? [];
            _channels = RoomChannels.Normalize(read, RoomChannels.MaxPerServer);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Servidor: os canais guardados não abriram ({exception.Message}). Começando sem canais.");
        }
        try
        {
            if (_renamesFile is not null && File.Exists(_renamesFile))
                _renames = (JsonSerializer.Deserialize<List<KeyValuePair<string, string>>>(File.ReadAllText(_renamesFile), Json) ?? [])
                    .Where(r => r.Key is { Length: > 0 } && RoomChannels.Create(RoomChannelKind.Text, r.Value) is not null)
                    .Select(r => new KeyValuePair<string, string>(RoomChannels.Key(r.Key), RoomChannels.CleanName(RoomChannelKind.Text, r.Value)))
                    .TakeLast(MaxRenames).ToList();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Servidor: a lista de canais renomeados não abriu ({exception.Message}).");
        }
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            Write(_file, JsonSerializer.Serialize(_channels, Json));
            if (_renamesFile is not null) Write(_renamesFile, JsonSerializer.Serialize(_renames, Json));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Servidor: não deu para guardar os canais: {exception.Message}");
        }
    }

    private static void Write(string file, string text)
    {
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, file, overwrite: true);
    }
}
