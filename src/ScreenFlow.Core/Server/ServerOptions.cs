using System.Net;

namespace ScreenFlow.Core.Server;

/// <summary>
/// Configuração do servidor ScreenFlow (1.4.1). Portas 0 abrem portas livres (usado nos testes). Sem
/// <see cref="DataDirectory"/>, o chat e os canais ficam só na memória.
/// </summary>
public sealed record ServerOptions
{
    public string Name { get; init; } = Environment.MachineName;
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public int ControlPort { get; init; } = AppInfo.DefaultServerControlPort;
    public int MediaPort { get; init; } = AppInfo.DefaultServerMediaPort;
    public int RoomPort { get; init; } = AppInfo.DefaultServerRoomPort;

    /// <summary>Senha do servidor (null: sem senha). Vale para transmitir, assistir e entrar na sala.</summary>
    public string? Password { get; init; }

    public int MaxStreams { get; init; } = 16;
    public int MaxViewersPerStream { get; init; } = 64;
    public int MaxMembers { get; init; } = 128;

    /// <summary>Pasta do chat guardado, dos canais e do id do servidor.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Quanto tempo quem assiste espera quem transmite voltar quando a conexão dele cai.</summary>
    public TimeSpan OfflineGrace { get; init; } = TimeSpan.FromMinutes(2);

    public bool HasPassword => !string.IsNullOrEmpty(Password);

    /// <summary>Nome, senha e portas conferidos (nome vazio vira o nome do PC; portas fora do lugar voltam ao padrão).</summary>
    public ServerOptions Normalized()
    {
        static int Port(int value, int fallback) => value is >= 0 and <= 65535 ? value : fallback;
        var name = Room.RoomPacket.CleanName(Name);
        return this with
        {
            Name = name.Length > 0 ? name : Room.RoomPacket.CleanName(Environment.MachineName) is { Length: > 0 } machine ? machine : "Servidor ScreenFlow",
            Password = string.IsNullOrEmpty(Password) ? null : Password,
            ControlPort = Port(ControlPort, AppInfo.DefaultServerControlPort),
            MediaPort = Port(MediaPort, AppInfo.DefaultServerMediaPort),
            RoomPort = Port(RoomPort, AppInfo.DefaultServerRoomPort),
            MaxStreams = Math.Clamp(MaxStreams, 1, 64),
            MaxViewersPerStream = Math.Clamp(MaxViewersPerStream, 1, 256),
            MaxMembers = Math.Clamp(MaxMembers, 2, 512),
        };
    }
}
