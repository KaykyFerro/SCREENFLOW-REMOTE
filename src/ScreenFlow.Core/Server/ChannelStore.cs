using System.Text.Json;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;

namespace ScreenFlow.Core.Server;

/// <summary>
/// Os canais de um servidor ScreenFlow (1.4.1), guardados num arquivo: qualquer pessoa no servidor cria, renomeia e apaga,
/// e eles continuam lá quando o servidor reinicia. O chat "geral" e a voz "Geral" existem sempre e não mudam (as versões
/// antigas só conhecem eles).
/// </summary>
public sealed class ChannelStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string? _file;
    private readonly object _gate = new();
    private List<RoomChannel> _channels = [];

    public ChannelStore(string? file)
    {
        _file = file;
        Load();
    }

    public IReadOnlyList<RoomChannel> All
    {
        get { lock (_gate) return _channels.ToArray(); }
    }

    /// <summary>Muda a lista. Retorna null se deu certo, ou o motivo de não ter mudado.</summary>
    public string? Apply(RoomChannelOperation operation, RoomChannel channel, RoomChannel? renamed = null)
    {
        lock (_gate)
        {
            var index = _channels.FindIndex(c => c.Same(channel));
            switch (operation)
            {
                case RoomChannelOperation.Create:
                    if (channel.IsDefault || index >= 0) return "Esse canal já existe.";
                    if (_channels.Count >= RoomChannels.MaxPerServer) return $"O servidor já tem {RoomChannels.MaxPerServer} canais. Apague um para criar outro.";
                    _channels.Add(channel);
                    break;
                case RoomChannelOperation.Rename:
                    if (renamed is null || channel.IsDefault || renamed.IsDefault) return "O chat geral e a voz Geral não mudam de nome.";
                    if (index < 0) return "Esse canal não existe mais.";
                    if (renamed.Kind != channel.Kind) return "Um canal não muda de tipo.";
                    var clash = _channels.FindIndex(c => c.Same(renamed));
                    if (clash >= 0 && clash != index) return "Já existe um canal com esse nome.";
                    _channels[index] = renamed;
                    break;
                case RoomChannelOperation.Delete:
                    if (channel.IsDefault) return "O chat geral e a voz Geral não podem ser apagados.";
                    if (index < 0) return "Esse canal não existe mais.";
                    _channels.RemoveAt(index);
                    break;
                default:
                    return "Operação desconhecida.";
            }
            Save();
            return null;
        }
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
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_channels, Json));
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Servidor: não deu para guardar os canais: {exception.Message}");
        }
    }
}
