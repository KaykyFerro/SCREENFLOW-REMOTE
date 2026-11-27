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
