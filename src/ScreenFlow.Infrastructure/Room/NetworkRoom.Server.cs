using System.Net;
using System.Net.Sockets;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;

namespace ScreenFlow.Infrastructure.Room;

/// <summary>Como está a sala pelo servidor ScreenFlow (1.4.1).</summary>
public enum RoomServerState { Connecting, Connected, NeedsPassword, WrongPassword, NoAnswer }

/// <summary>O servidor da sala visto daqui: nome, estado, quantas pessoas e os canais dele.</summary>
public sealed record RoomServerStatus(string Address, RoomServerState State, string Text, string? Name, int Members, int Streams, IReadOnlyList<RoomChannel> Channels);

/// <summary>
/// A sala pelo servidor ScreenFlow (1.4.1). Em vez de cada PC falar com cada outro, tudo vai para o servidor num
/// envelope com a lista de quem recebe (vazia: todo mundo) e o servidor repassa. O pacote de dentro é o mesmo da sala
/// direta (na sala privada, lacrado: o servidor repassa sem conseguir ler). O servidor guarda o chat aberto e manda o
/// histórico para quem entra, e os canais são do servidor (qualquer um cria, renomeia e apaga).
/// </summary>
public sealed partial class NetworkRoom
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KeepAliveProbe = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServerSilence = TimeSpan.FromSeconds(8);

    /// <summary>Quantas vezes a prova da senha vai para o mesmo desafio (o UDP pode perder uma; senha errada para aqui).</summary>
    private const int MaxProofsPerChallenge = 3;
    private volatile ServerLink? _server;

    /// <summary>A sala está indo pelo servidor (mesmo que ainda não tenha entrado).</summary>
    public bool ViaServer => _server is not null;

    /// <summary>O estado da sala pelo servidor (null: sala direta da rede).</summary>
    public RoomServerStatus? ServerStatus => _server?.Status();

    /// <summary>Os canais do servidor (vazio fora do servidor ou antes de entrar).</summary>
    public IReadOnlyList<RoomChannel> ServerChannels => _server?.Channels ?? [];

    /// <summary>O estado do servidor, o nome ou os canais dele mudaram. Chamado fora da thread da interface.</summary>
    public event Action? ServerChanged;

    /// <summary>
    /// Passa a sala para o servidor em <paramref name="server"/> (null volta para a sala direta da rede). Quem estava na
    /// sala de antes some da lista; quem está no servidor aparece em instantes.
    /// </summary>
    public void UseServer(IPEndPoint? server, string? password)
    {
        var previous = _server;
        if (previous is null && server is null) return;
        if (previous is not null && server is not null && previous.EndPoint.Equals(server) && previous.Password == password) return;
        if (previous is not null && previous.Authorized)
        {
            try { SendRelay(RoomPacket.Leave(SelfId), null, previous); } catch { }
        }
        _server = server is null ? null : new ServerLink(server, string.IsNullOrEmpty(password) ? null : password);
        foreach (var peer in _peers.All())
            if (_peers.Remove(peer.Id, out var gone) && gone is not null) OnPeerGone(gone, server is null ? "ficou no servidor" : "ficou na sala direta");
        _hosts.Clear();
        _historyAsked.Clear();
        _wantsReplyTicks = 2;
        _announceSoon = true;
        Log.Info(server is null ? "Sala da rede: de volta à sala direta da rede." : $"Sala da rede: pelo servidor {server}.");
        if (_server is { } link) Probe(link, DateTimeOffset.UtcNow);
        HostsChanged?.Invoke();
        ServerChanged?.Invoke();
    }

    /// <summary>Cria, renomeia ou apaga um canal do servidor (qualquer pessoa no servidor pode). false fora do servidor.</summary>
    public bool ChangeServerChannel(RoomChannelOperation operation, RoomChannelKind kind, string name, string? newName = null)
    {
        if (_server is not { Authorized: true } server) return false;
        SendRaw(RoomPacket.ChannelOp(SelfId, operation, kind, name, newName), server.EndPoint);
        return true;
    }

    // ───────────────────────── Envio pelo servidor ou direto ─────────────────────────

    /// <summary>Para uma pessoa: pelo servidor (com o id dela) ou direto para o endereço dela.</summary>
    private void SendTo(ReadOnlySpan<byte> packet, RoomPeer peer) => SendTo(packet, peer.Id, peer.EndPoint);

    private void SendTo(ReadOnlySpan<byte> packet, Guid id, IPEndPoint endpoint)
    {
        if (_server is { } server) SendRelay(packet, [id], server);
        else Send(packet, endpoint);
    }

    /// <summary>Para várias pessoas: pelo servidor, um envelope por até 32 pessoas; direto, um pacote para cada uma.</summary>
    private void SendToMany(ReadOnlySpan<byte> packet, IReadOnlyList<RoomPeer> peers)
    {
        if (peers.Count == 0) return;
        if (_server is { } server)
        {
            for (var start = 0; start < peers.Count; start += RoomPacket.MaxRelayTargets)
            {
                var targets = new Guid[Math.Min(RoomPacket.MaxRelayTargets, peers.Count - start)];
                for (var i = 0; i < targets.Length; i++) targets[i] = peers[start + i].Id;
                SendRelay(packet, targets, server);
            }
            return;
        }
        foreach (var peer in peers) Send(packet, peer.EndPoint);
    }

    /// <summary>O envelope para o servidor: o pacote de dentro vai lacrado na sala privada, o envelope nunca.</summary>
    private void SendRelay(ReadOnlySpan<byte> packet, IReadOnlyCollection<Guid>? targets, ServerLink server)
    {
        var inner = packet.Length > RoomPacket.HeaderSize && packet[3] != (byte)RoomPacketType.Sealed ? Keys.Seal(packet, SelfId) : null;
        SendRaw(RoomPacket.Relay(SelfId, inner is null ? packet : inner, targets), server.EndPoint);
    }

    /// <summary>Manda como está (sem lacrar): os pacotes para o próprio servidor.</summary>
    private void SendRaw(ReadOnlySpan<byte> packet, IPEndPoint target) => Transmit(packet, target);

    // ───────────────────────── Entrada no servidor ─────────────────────────

    /// <summary>A cada 100 ms: sonda o servidor até entrar (e de vez em quando depois) e percebe quando ele some.</summary>
    private void ServerTick(ServerLink server, DateTimeOffset now)
    {
        if (server.Authorized && now - server.LastHeard > ServerSilence)
        {
            server.Authorized = false;
            server.State = RoomServerState.NoAnswer;
            Log.Info($"Sala da rede: o servidor {server.EndPoint} parou de responder.");
            ServerChanged?.Invoke();
        }
        if (now - server.LastProbe >= (server.Authorized ? KeepAliveProbe : ProbeInterval)) Probe(server, now);
    }

    private void Probe(ServerLink server, DateTimeOffset now)
    {
        server.LastProbe = now;
        // Com o desafio do servidor e a senha, manda a prova: até 3 vezes por desafio (uma pode se perder no caminho), e
        // não mais que isso (senha errada não vira repetição sem fim).
        byte[]? proof = null;
        if (!server.Authorized && server.Password is { } password && server.Info is { PasswordRequired: true } info)
        {
            if (server.ProvedNonce != info.Nonce)
            {
                server.ProvedNonce = info.Nonce;
                server.ProofsSent = 0;
            }
            if (server.ProofsSent < MaxProofsPerChallenge)
            {
                proof = RoomPacket.ServerProof(password, info.Nonce, SelfId);
                server.ProofsSent++;
            }
        }
        SendRaw(RoomPacket.ServerProbe(SelfId, AppInfo.DisplayVersion, proof), server.EndPoint);
    }

    private void OnServerPacket(ServerLink server, RoomPacketType type, Guid sender, ReadOnlySpan<byte> data, DateTimeOffset now)
    {
        var body = RoomPacket.Body(data);
        switch (type)
        {
            case RoomPacketType.ServerInfo when RoomPacket.TryReadServerInfo(body, out var info):
                OnServerInfo(server, sender, info, now);
                break;
            case RoomPacketType.Presence when RoomPacket.TryReadPresence(body, out var presence) && presence.IsServer:
                server.LastHeard = now;
                var channels = presence.CreatedChannels.ToList();
                if (!server.Channels.SequenceEqual(channels) || server.Name != presence.Name)
                {
                    server.Channels = channels;
                    server.Name = presence.Name;
                    ServerChanged?.Invoke();
                }
                break;
            case RoomPacketType.Chat when RoomPacket.TryReadChat(body, out var message) && message.History && !Keys.Active:
                // O chat guardado no servidor (aberto). Na sala privada, o servidor não tem nada dela para mostrar.
                server.LastHeard = now;
                if (_historyAsked.TryGetValue(sender, out var asked) && now - asked < TimeSpan.FromSeconds(20) && _history.Add(message) >= 0)
                {
                    if (message.Image is { } image) MaybeFetch(image.Blob, now, message.AuthorId);
                    MessageReceived?.Invoke(message);
                }
                break;
        }
    }

    private void OnServerInfo(ServerLink server, Guid sender, RoomServerInfo info, DateTimeOffset now)
    {
        if (server.Id != sender)
        {
            if (server.Id != Guid.Empty) Log.Info("Sala da rede: o servidor reiniciou (id novo).");
            server.Id = sender;
        }
        var previous = server.Info;
        server.Info = info;
        server.LastHeard = now;
        var state = server.State;
        if (info.Authorized)
        {
            if (!server.Authorized)
            {
                server.Authorized = true;
                server.State = RoomServerState.Connected;
                Log.Info($"Sala da rede: entrou no servidor {info.Name} ({server.EndPoint}).");
                // Avisa todo mundo que chegou e pede o chat guardado.
                _announceSoon = true;
                _historyAsked[sender] = now;
                SendRaw(RoomPacket.HistoryRequest(SelfId), server.EndPoint);
            }
        }
        else
        {
            var wasIn = server.Authorized;
            server.Authorized = false;
            // Estava dentro e o servidor esqueceu este PC (ficou um tempo sem sinal ou trocou de endereço): prova a senha de novo.
            if (wasIn) server.ProofsSent = 0;
            if (!info.PasswordRequired) server.State = RoomServerState.Connecting; // o servidor esqueceu (reiniciou): a próxima sonda entra
            else if (server.Password is null) server.State = RoomServerState.NeedsPassword;
            else if (server.ProvedNonce == info.Nonce && server.ProofsSent >= MaxProofsPerChallenge && !wasIn) server.State = RoomServerState.WrongPassword;
            else server.State = RoomServerState.Connecting;
            // Desafio novo (ou o servidor reiniciou): manda a prova já. Falta tentar de novo: no máximo a cada 300 ms.
            var prove = server.Password is not null && info.PasswordRequired;
            if (prove && server.ProvedNonce != info.Nonce) Probe(server, now);
            else if ((!info.PasswordRequired || (prove && server.ProofsSent < MaxProofsPerChallenge)) && now - server.LastProbe > TimeSpan.FromMilliseconds(300))
                Probe(server, now);
        }
        if (state != server.State || previous is null || previous.Members != info.Members || previous.Streams != info.Streams || previous.Name != info.Name)
            ServerChanged?.Invoke();
    }

    private sealed class ServerLink(IPEndPoint endPoint, string? password)
    {
        public IPEndPoint EndPoint { get; } = endPoint;
        public string? Password { get; } = password;
        public Guid Id;
        public volatile RoomServerInfo? Info;
        public volatile bool Authorized;
        public volatile RoomServerState State = RoomServerState.Connecting;
        public volatile string? Name;
        public volatile string? ProvedNonce;
        public int ProofsSent;
        public volatile IReadOnlyList<RoomChannel> Channels = [];
        public DateTimeOffset LastHeard = DateTimeOffset.UtcNow;
        public DateTimeOffset LastProbe = DateTimeOffset.MinValue;

        public RoomServerStatus Status()
        {
            var info = Info;
            var name = Name ?? info?.Name;
            var label = name ?? EndPoint.Address.ToString();
            var text = State switch
            {
                RoomServerState.Connected => info is null ? $"No servidor {label}." : info.Members switch
                {
                    <= 1 => $"No servidor {label} (só você por enquanto).",
                    var n => $"No servidor {label} ({n} pessoas).",
                },
                RoomServerState.NeedsPassword => $"O servidor {label} pede senha. Coloque a senha em Configurações › Servidor.",
                RoomServerState.WrongPassword => $"Senha do servidor {label} incorreta. Confira em Configurações › Servidor.",
                RoomServerState.NoAnswer => $"O servidor {label} parou de responder. Tentando de novo…",
                _ => $"Conectando ao servidor {label}…",
            };
            return new RoomServerStatus(EndPoint.Address.ToString(), State, text, name, info?.Members ?? 0, info?.Streams ?? 0, Channels);
        }
    }
}
