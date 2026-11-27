using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ScreenFlow.Core.Files;

/// <summary>A resposta de quem tem o arquivo.</summary>
public enum FileTransferStatus : byte
{
    Ok = 0,
    /// <summary>Não tem (nunca teve, parou de compartilhar, ou o pedido não provou ter a chave).</summary>
    NotFound = 1,
    /// <summary>O arquivo mudou ou sumiu do PC de quem mandou.</summary>
    Changed = 2,
    /// <summary>Muita gente baixando ao mesmo tempo: tente daqui a pouco.</summary>
    Busy = 3,
    BadRequest = 5,
}

/// <summary>
/// (1.5.0) A conversa de um download de arquivo do chat, por TCP direto entre os dois PCs (porta 47803):
/// <code>
/// pede:     "SFF1" | id do arquivo (16) | a partir de (8) | sorteio de quem pede (16) | prova (16)
/// responde: "SFF1" | situação (1) | tamanho (8) | sorteio de quem tem (16)
/// e depois, bloco a bloco: tamanho (4) | bloco cifrado | etiqueta (16)
/// </code>
/// A prova é um HMAC com a chave do anexo (que só vai dentro da mensagem): quem não tem a mensagem não consegue nem pedir.
/// Cada download cifra com uma chave só dele (HKDF da chave do anexo com os dois sorteios), e o número do bloco vai no
/// nonce do AES-GCM, então um bloco trocado, mexido ou fora de ordem não passa.
/// </summary>
public static class FileTransferProtocol
{
    public const int ChunkBytes = 256 * 1024;
    /// <summary>O maior arquivo que dá para mandar pelo chat (4 GB).</summary>
    public const long MaxFileBytes = 4L * 1024 * 1024 * 1024;
    public const int ShareBytes = 16;
    public const int KeyBytes = 32;
    public const int HashBytes = 32;
    public const int RandomBytes = 16;
    public const int TokenBytes = 16;
    public const int TagBytes = 16;
    public const int NonceBytes = 12;
    public const int RequestBytes = 4 + ShareBytes + 8 + RandomBytes + TokenBytes;
    public const int ResponseBytes = 4 + 1 + 8 + RandomBytes;
    public const int FrameHeaderBytes = 4;

    private static ReadOnlySpan<byte> Magic => "SFF1"u8;
    private static ReadOnlySpan<byte> TokenLabel => "ScreenFlow arquivo pedido v1"u8;
    private static ReadOnlySpan<byte> SessionLabel => "ScreenFlow arquivo blocos v1"u8;

    public static long ChunkCount(long size) => size <= 0 ? 0 : (size + ChunkBytes - 1) / ChunkBytes;

    public static int ChunkLength(long size, long index) => (int)Math.Clamp(size - index * ChunkBytes, 0, ChunkBytes);

    /// <summary>O pedido de <paramref name="share"/> a partir de <paramref name="offset"/> (sempre no começo de um bloco).</summary>
    public static byte[] Request(ReadOnlySpan<byte> share, long offset, ReadOnlySpan<byte> clientRandom, ReadOnlySpan<byte> key)
    {
        if (share.Length != ShareBytes || clientRandom.Length != RandomBytes || key.Length != KeyBytes) throw new ArgumentException("Pedido inválido.");
        var buffer = new byte[RequestBytes];
        Magic.CopyTo(buffer);
        share.CopyTo(buffer.AsSpan(4));
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(4 + ShareBytes), offset);
        clientRandom.CopyTo(buffer.AsSpan(12 + ShareBytes));
        Token(key, share, offset, clientRandom).CopyTo(buffer.AsSpan(12 + ShareBytes + RandomBytes));
        return buffer;
    }

    public static bool TryReadRequest(ReadOnlySpan<byte> data, out byte[] share, out long offset, out byte[] clientRandom, out byte[] token)
    {
        share = clientRandom = token = [];
        offset = 0;
        if (data.Length != RequestBytes || !data[..4].SequenceEqual(Magic)) return false;
        share = data.Slice(4, ShareBytes).ToArray();
        offset = BinaryPrimitives.ReadInt64BigEndian(data[(4 + ShareBytes)..]);
        clientRandom = data.Slice(12 + ShareBytes, RandomBytes).ToArray();
        token = data.Slice(12 + ShareBytes + RandomBytes, TokenBytes).ToArray();
        return offset >= 0;
    }

    /// <summary>O pedido veio de quem tem a chave do anexo.</summary>
    public static bool CheckToken(ReadOnlySpan<byte> key, ReadOnlySpan<byte> share, long offset, ReadOnlySpan<byte> clientRandom, ReadOnlySpan<byte> token)
        => key.Length == KeyBytes && token.Length == TokenBytes && CryptographicOperations.FixedTimeEquals(Token(key, share, offset, clientRandom), token);

    public static byte[] Response(FileTransferStatus status, long size, ReadOnlySpan<byte> serverRandom)
    {
        var buffer = new byte[ResponseBytes];
        Magic.CopyTo(buffer);
        buffer[4] = (byte)status;
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(5), size);
        if (serverRandom.Length == RandomBytes) serverRandom.CopyTo(buffer.AsSpan(13));
        return buffer;
    }

    public static bool TryReadResponse(ReadOnlySpan<byte> data, out FileTransferStatus status, out long size, out byte[] serverRandom)
    {
        status = FileTransferStatus.BadRequest;
        size = 0;
        serverRandom = [];
        if (data.Length != ResponseBytes || !data[..4].SequenceEqual(Magic)) return false;
        status = (FileTransferStatus)data[4];
        size = BinaryPrimitives.ReadInt64BigEndian(data[5..]);
        serverRandom = data.Slice(13, RandomBytes).ToArray();
        return Enum.IsDefined(status) && size >= 0;
    }

    /// <summary>A chave deste download (cada conexão tem a sua, então nunca se repete um nonce).</summary>
    public static byte[] SessionKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> share, ReadOnlySpan<byte> clientRandom, ReadOnlySpan<byte> serverRandom)
    {
        var salt = new byte[RandomBytes * 2];
        clientRandom.CopyTo(salt);
        serverRandom.CopyTo(salt.AsSpan(RandomBytes));
        var info = new byte[SessionLabel.Length + share.Length];
        SessionLabel.CopyTo(info);
        share.CopyTo(info.AsSpan(SessionLabel.Length));
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, key.ToArray(), KeyBytes, salt, info);
    }

    /// <summary>O nonce do bloco <paramref name="index"/>.</summary>
    public static void WriteNonce(long index, Span<byte> nonce)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], index);
    }

    private static byte[] Token(ReadOnlySpan<byte> key, ReadOnlySpan<byte> share, long offset, ReadOnlySpan<byte> clientRandom)
    {
        var data = new byte[TokenLabel.Length + share.Length + 8 + clientRandom.Length];
        TokenLabel.CopyTo(data);
        share.CopyTo(data.AsSpan(TokenLabel.Length));
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(TokenLabel.Length + share.Length), offset);
        clientRandom.CopyTo(data.AsSpan(TokenLabel.Length + share.Length + 8));
        return HMACSHA256.HashData(key, data).AsSpan(0, TokenBytes).ToArray();
    }
}
