using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Security;

namespace ScreenFlow.Infrastructure.Session;

/// <summary>
/// Transmitir pelo servidor ScreenFlow (1.4.1). Quem transmite conecta no servidor, prova a senha dele e, daí em diante,
/// o servidor faz o papel de uma pessoa assistindo: recebe uma cópia só da tela e manda para cada pessoa que assiste por
/// ele. Para a captura, o encoder e o ABR, o servidor é como qualquer pessoa assistindo, com os limites e a pior rede de
/// quem está assistindo por ele. Se a conexão cair, tenta de novo sozinho (quem assiste espera no servidor).
/// </summary>
public sealed partial class HostSession
{
    private async Task PublishLoopAsync()
    {
        var server = _options.Server!;
        var token = _cancellation.Token;
        var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));
        while (!token.IsCancellationRequested)
        {
            var stop = false;
            try
            {
                SetServerState(ServerLinkState.Connecting, backoff.Attempt == 0 ? $"Conectando ao servidor {server.Host}…" : $"Tentando de novo o servidor {server.Host}…");
                stop = await PublishOnceAsync(server, backoff, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or System.Text.Json.JsonException
                                                  or TimeoutException or ObjectDisposedException or OperationCanceledException)
            {
                Log.Info($"Servidor: {exception.Message}");
                SetServerState(ServerLinkState.Connecting, exception switch
                {
                    SocketException { SocketErrorCode: SocketError.ConnectionRefused } => $"O servidor {server.Host} não respondeu (o programa do servidor está aberto?). Tentando de novo…",
                    SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => $"Não achei o servidor {server.Host}. Confira o endereço em Configurações › Servidor.",
                    SocketException or TimeoutException or OperationCanceledException => $"Não deu para alcançar o servidor {server.Host} (Tailscale ou Radmin ligado?). Tentando de novo…",
                    _ => "A conexão com o servidor caiu. Tentando de novo…",
                });
            }
            catch (Exception exception)
            {
                Log.Error("Servidor: erro ao transmitir pelo servidor", exception);
                SetServerState(ServerLinkState.Connecting, $"Erro com o servidor ({exception.Message}). Tentando de novo…");
            }
            finally
            {
                CloseServerSocket();
            }
            if (stop) return;
            try { await Task.Delay(backoff.Next(), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Uma conexão com o servidor, até cair. true: não adianta tentar de novo (senha errada, versão diferente).</summary>
    private async Task<bool> PublishOnceAsync(ServerPublish server, Backoff backoff, CancellationToken token)
    {
        var address = await LocalNetworks.ResolveAsync(server.Host, token) ?? throw new SocketException((int)SocketError.HostNotFound);
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(6));
            try { await client.ConnectAsync(address, server.Port, connectTimeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(); }
        }
        var stream = client.GetStream();
        using var exchange = new KeyExchange();
        await ControlFraming.WriteAsync(stream, new PublishMessage(AppInfo.ProtocolVersion, AppInfo.DisplayVersion, _options.DeviceName, server.StreamId.ToString("N"),
            exchange.PublicKey, _options.Mode, HasCameraOverlay, AudioEnabled), token);

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(20));
        var first = await ControlFraming.ReadAsync(stream, handshake.Token);
        if (first is RejectedMessage early) return Refused(early);
        if (first is not ChallengeMessage challenge) throw new InvalidDataException("Resposta inesperada do servidor.");
        if (challenge.PinRequired && string.IsNullOrEmpty(server.Password))
        {
            SetServerState(ServerLinkState.NeedsPassword, $"O servidor {challenge.HostName} pede senha: coloque a senha em Configurações › Servidor.");
            return true;
        }
        var secret = exchange.DeriveSharedSecret(challenge.PublicKey);
        var transcript = SessionCrypto.Transcript(exchange.PublicKey, challenge.PublicKey, challenge.Nonce);
        await ControlFraming.WriteAsync(stream, new AuthMessage(SessionCrypto.CreateProof(secret, challenge.PinRequired ? server.Password : null, transcript)), token);
        CryptographicOperations.ZeroMemory(secret);

        var reply = await ControlFraming.ReadAsync(stream, handshake.Token);
        if (reply is RejectedMessage rejected) return Refused(rejected);
        if (reply is not PublishAcceptedMessage accepted) throw new InvalidDataException("Resposta inesperada do servidor.");
        if (await ControlFraming.ReadAsync(stream, handshake.Token) is not HelloMessage hello) throw new InvalidDataException("O servidor não seguiu o combinado.");

        // A mídia para o servidor sai de um socket próprio, sem endereço fixo: o Windows escolhe a rede que chega no
        // servidor (o socket da transmissão fica no endereço da rede escolhida na tela, que pode não chegar lá).
        var socket = MediaSender.CreateUdp(IPAddress.Any, 0);
        var sender = new MediaSender(socket);
        _serverSocket = socket;
        _serverSender = sender;
        _ = Task.Run(() => DrainServerSocketAsync(socket, token), token);

        backoff.Reset();
        _serverName = Core.Room.RoomPacket.CleanName(accepted.ServerName) is { Length: > 0 } clean ? clean : server.Host;
        SetServerState(ServerLinkState.Live, $"No servidor {_serverName}: quem está nele já pode assistir.");
        await ServeViewerAsync(client, stream, (IPEndPoint)client.Client.RemoteEndPoint!, hello, _serverName, sender, token);
        if (!token.IsCancellationRequested) SetServerState(ServerLinkState.Connecting, "A conexão com o servidor caiu. Tentando de novo…");
        return false;
    }

    /// <summary>O servidor recusou: senha errada e versão diferente param de tentar; servidor cheio tenta de novo.</summary>
    private bool Refused(RejectedMessage rejected)
    {
        Log.Warn($"Servidor recusou a transmissão: {rejected.Code} {rejected.Message}");
        switch (rejected.Code)
        {
            case RejectedMessage.WrongPin:
                SetServerState(ServerLinkState.NeedsPassword, "Senha do servidor incorreta. Confira em Configurações › Servidor.");
                return true;
            case RejectedMessage.VersionMismatch:
                SetServerState(ServerLinkState.Refused, rejected.Message);
                return true;
            default:
                SetServerState(ServerLinkState.Connecting, $"{rejected.Message} Tentando de novo…");
                return false;
        }
    }

    /// <summary>Quem assiste pelo servidor mudou: o codec, a resolução e o FPS acompanham o que todos lá aguentam.</summary>
    private void OnServerAudience(RemoteViewer server, AudienceMessage audience)
    {
        var before = server.Audience;
        server.Audience = Math.Clamp(audience.Viewers, 0, 10_000);
        server.AudienceNames = [.. (audience.Names ?? []).Select(n => Core.Room.RoomPacket.CleanName(n)).Where(n => n.Length > 0).Take(64)];
        var decoders = (audience.Decoders ?? []).Where(Enum.IsDefined).Distinct().ToArray();
        server.Hello = server.Hello with
        {
            Decoders = decoders.Length > 0 ? decoders : server.Hello.Decoders,
            MaxWidth = Math.Clamp(audience.MaxWidth, 320, 7680),
            MaxHeight = Math.Clamp(audience.MaxHeight, 240, 4320),
            MaxFps = Math.Clamp(audience.MaxFps, 1, 480),
        };
        if (!Renegotiate(null)) Log.Warn("Servidor: ninguém em comum entre os codecs de quem assiste pelo servidor e os deste PC.");
        if (server.Audience > 0 && before == 0)
        {
            _keyframeRequested = true;
            _cameraKeyframeRequested = true;
        }
        _wake.Set();
        _cameraWake.Set();
        UpdateAudioPause();
        if (_pacer is { } pacer) pacer.BytesPerSecond = PacingRate();
        if (server.Audience != before)
        {
            var text = server.Audience switch
            {
                0 => $"No servidor {_serverName}: ninguém assistindo por lá ainda.",
                1 => $"No servidor {_serverName}: 1 pessoa assistindo ({string.Join(", ", server.AudienceNames)}).",
                var n => $"No servidor {_serverName}: {n} pessoas assistindo.",
            };
            SetServerState(ServerLinkState.Live, text);
        }
        ViewersChanged?.Invoke();
    }

    /// <summary>Lê (e descarta) os pacotes que abrem o caminho, que o servidor manda para o socket da mídia.</summary>
    private static async Task DrainServerSocketAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            try { await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!socket.IsBound) break; }
        }
    }

    private void CloseServerSocket()
    {
        var socket = Interlocked.Exchange(ref _serverSocket, null);
        _serverSender = null;
        socket?.Dispose();
    }
}
