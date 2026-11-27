using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;
using ScreenFlow.Infrastructure.Network;

namespace ScreenFlow.Infrastructure.Room;

/// <summary>
/// (1.5.0) Uma sala como servidor de uma rede do Radmin: tudo lacrado com a chave do servidor (quem não tem a chave não
/// vê nem ouve nada, mesmo na mesma rede), os canais guardados em todos os PCs (<see cref="SpaceChannels"/>), e cada um
/// conta para os outros quem mais está no servidor, para que todos se achem mesmo quando o broadcast não chega.
/// </summary>
public sealed partial class NetworkRoom
{
    private static readonly TimeSpan GossipInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TableInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CandidateLife = TimeSpan.FromMinutes(2);
    private const int MaxCandidates = 64;

    private readonly ConcurrentDictionary<IPEndPoint, DateTimeOffset> _candidates = new();
    private readonly RateLimiter<Guid> _tableLimit = new(burst: 8, perSecond: 2);
    private readonly RateLimiter<Guid> _gossipLimit = new(burst: 2, perSecond: 0.2);
    private DateTimeOffset _lastGossip = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastTable = DateTimeOffset.UtcNow;
    private (DateTimeOffset At, (uint Network, uint Mask)[] Nets) _localNets;

    /// <summary>Os canais do servidor (null: esta sala não é um servidor por rede).</summary>
    public SpaceChannelTable? SpaceChannels { get; init; }

    /// <summary>É um servidor por rede do Radmin (1.5.0).</summary>
    public bool IsSpace => SpaceChannels is not null;

    /// <summary>Os canais do servidor mudaram por causa de alguém (chamado fora da thread da interface).</summary>
    public event Action? SpaceChannelsChanged;

    /// <summary>Passa a usar a chave do servidor: a partir daqui tudo vai e vem lacrado com ela.</summary>
    public void UseSpaceKey(byte[] secret)
    {
        Keys.Use(secret, 0, DateTimeOffset.UtcNow);
        _announceSoon = true;
    }

    /// <summary>Manda os canais que mudaram aqui para todo mundo do servidor, na hora.</summary>
    public void PublishChannels(IReadOnlyList<SpaceChannel> changed)
    {
        if (changed.Count == 0) return;
        var peers = _peers.All().ToList();
        foreach (var packet in RoomPacket.ChannelTable(SelfId, changed)) SendToMany(packet, peers);
    }

    /// <summary>
    /// A rede voltou (ou o servidor acabou de conectar): chama todo mundo de novo pedindo resposta, lê os adaptadores
    /// outra vez e pede as mensagens recentes para os primeiros que responderem.
    /// </summary>
    public void Reconnect()
    {
        _wantsReplyTicks = 3;
        _broadcastsAt = DateTimeOffset.MinValue;
        _started = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _historyRequests, 0);
        _announceSoon = true;
    }

    /// <summary>Põe mensagens guardadas neste PC no histórico da sala (quem chegar e pedir recebe também), sem avisar.</summary>
    public void Preload(IEnumerable<RoomChatMessage> messages)
    {
        foreach (var message in messages) _history.Add(message with { History = false });
    }

    private void SpaceTick(DateTimeOffset now)
    {
        if (now - _lastGossip >= GossipInterval)
        {
            _lastGossip = now;
            var peers = _peers.All().ToList();
            if (peers.Count > 1)
            {
                var packet = RoomPacket.PeerList(SelfId, peers.Select(p => new RoomPeerHint(p.Id, p.EndPoint)).ToList());
                SendToMany(packet, peers);
            }
        }
        if (now - _lastTable >= TableInterval)
        {
            _lastTable = now;
            SpaceChannels!.Expire(now);
            PublishChannels(SpaceChannels.Snapshot());
        }
        foreach (var (endpoint, until) in _candidates)
            if (now > until) _candidates.TryRemove(endpoint, out _);
    }

    /// <summary>Quem acabou de aparecer recebe os canais e a lista de quem mais está no servidor.</summary>
    private void OnSpacePeerJoined(RoomPeer peer)
    {
        foreach (var packet in RoomPacket.ChannelTable(SelfId, SpaceChannels!.Snapshot())) SendTo(packet, peer);
        var others = _peers.All().Where(p => p.Id != peer.Id).Select(p => new RoomPeerHint(p.Id, p.EndPoint)).ToList();
        if (others.Count > 0) SendTo(RoomPacket.PeerList(SelfId, others), peer);
    }

    private void OnChannelTable(Guid sender, ReadOnlySpan<byte> body, IPEndPoint from, DateTimeOffset now)
    {
        if (_peers.Find(sender) is null || !_tableLimit.TryTake(sender, now)) return;
        if (!RoomPacket.TryReadChannelTable(body, out var entries)) return;
        var changed = false;
        foreach (var entry in entries) changed |= SpaceChannels!.Merge(entry, now);
        if (changed) SpaceChannelsChanged?.Invoke();
    }

    /// <summary>
    /// Alguém do servidor contou quem mais está nele: quem este PC ainda não conhece passa a receber a presença direto
    /// por uns minutos (só endereços das redes deste PC, para ninguém usar isso para mandar pacotes para fora).
    /// </summary>
    private void OnPeerList(Guid sender, ReadOnlySpan<byte> body, IPEndPoint from, DateTimeOffset now)
    {
        if (_peers.Find(sender) is null || !_gossipLimit.TryTake(sender, now)) return;
        if (!RoomPacket.TryReadPeerList(body, out var hints)) return;
        var added = 0;
        foreach (var hint in hints)
        {
            if (hint.Id == SelfId || _blocked.ContainsKey(hint.Id) || _peers.Find(hint.Id) is not null) continue;
            if (_candidates.Count >= MaxCandidates || !IsLocalNetwork(hint.EndPoint.Address)) continue;
            if (_candidates.TryAdd(hint.EndPoint, now + CandidateLife)) added++;
        }
        if (added > 0)
        {
            Log.Debug($"Servidor por rede: {added} PC(s) a chamar direto, contados por outra pessoa.");
            _wantsReplyTicks = Math.Max(_wantsReplyTicks, 2);
            _announceSoon = true;
        }
    }

    private IEnumerable<IPEndPoint> Candidates(DateTimeOffset now)
    {
        foreach (var (endpoint, until) in _candidates)
            if (now <= until) yield return endpoint;
    }

    /// <summary>O endereço é de uma das redes ligadas deste PC (Radmin, rede local, Tailscale).</summary>
    private bool IsLocalNetwork(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var value = ToUInt(address);
        if (value is 0 or uint.MaxValue || (value >> 28) == 0xE) return false; // vazio, broadcast ou multicast
        var now = DateTimeOffset.UtcNow;
        if (now - _localNets.At > TimeSpan.FromSeconds(15))
        {
            try
            {
                _localNets = (now, RadminNetwork.GetIPv4Adapters().Where(a => a.IsUp)
                    .Select(a => (ToUInt(a.Address) & ToUInt(a.Mask), ToUInt(a.Mask))).Distinct().ToArray());
            }
            catch (Exception exception)
            {
                Log.Debug($"Redes deste PC: {exception.Message}");
                _localNets = (now, []);
            }
        }
        foreach (var (network, mask) in _localNets.Nets)
            if (mask != 0 && (value & mask) == network) return true;
        return false;
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]) : 0;
    }
}
