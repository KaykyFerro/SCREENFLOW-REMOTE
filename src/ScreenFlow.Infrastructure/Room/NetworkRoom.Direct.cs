using ScreenFlow.Core.Room;

namespace ScreenFlow.Infrastructure.Room;

/// <summary>Um pacote do chat privado que chegou, com quem mandou e a chave que ele anuncia nesta sala.</summary>
public sealed record RoomDirectPacket(RoomPacketType Type, Guid Sender, string SenderName, string IdentityKey, byte[] Body);

/// <summary>
/// (1.5.0) O chat privado passa pela sala: a mensagem vai direto para a pessoa (ou pelo servidor ScreenFlow), lacrada
/// também com a chave da sala quando ela tem uma. Quem cifra, abre, guarda e confirma é o <see cref="DirectChat"/>.
/// </summary>
public sealed partial class NetworkRoom
{
    private readonly RateLimiter<Guid> _directLimit = new(burst: 24, perSecond: 8);

    /// <summary>A chave pública do chat privado deste PC (vai na presença). Null: este PC não recebe mensagens privadas.</summary>
    public string? IdentityKey { get; init; }

    /// <summary>
    /// Chegou uma mensagem privada (ou a confirmação de uma) para este PC, de alguém que está na sala e anuncia a chave
    /// do chat privado. Chamado na thread da rede.
    /// </summary>
    public event Action<RoomDirectPacket>? DirectReceived;

    /// <summary>A chave do chat privado que a pessoa anuncia nesta sala (null: não está aqui, ou está numa versão antiga).</summary>
    public string? IdentityKeyOf(Guid peer) => _peers.Find(peer)?.Presence.IdentityKey;

    /// <summary>
    /// (1.5.0) O IP de onde a pessoa fala com esta sala (para baixar os arquivos dela direto). Null pelo servidor
    /// ScreenFlow, em que tudo vem do servidor: aí valem os IPs que vêm dentro da mensagem.
    /// </summary>
    public System.Net.IPAddress? AddressOf(Guid peer) => _server is null ? _peers.Find(peer)?.EndPoint.Address : null;

    /// <summary>(1.5.0) O nome de quem está na sala com este IP (para "Fulano está baixando"). Null se ninguém.</summary>
    public string? NameAt(System.Net.IPAddress address)
        => _server is null ? _peers.All().FirstOrDefault(p => p.EndPoint.Address.Equals(address))?.Presence.Name : null;

    /// <summary>Manda um pacote do chat privado para a pessoa (direto, ou pelo servidor). false se ela não está nesta sala.</summary>
    public bool SendDirect(Guid peer, byte[] packet)
    {
        if (_cancellation.IsCancellationRequested || !IsRunning || _peers.Find(peer) is not { } target) return false;
        SendTo(packet, target);
        return true;
    }

    private void OnDirect(RoomPacketType type, Guid sender, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        // Só de quem já está na sala (com a presença) e anuncia a chave: é com ela que a mensagem abre.
        if (_peers.Find(sender) is not { Presence.IdentityKey: { } key } peer) return;
        if (!DirectCrypto.TryReadRecipient(body, out var recipient) || recipient != SelfId) return;
        if (!_directLimit.TryTake(sender, now)) return;
        DirectReceived?.Invoke(new RoomDirectPacket(type, sender, peer.Presence.Name, key, body.ToArray()));
    }
}
