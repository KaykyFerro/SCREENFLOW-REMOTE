using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ScreenFlow.Core.Files;
using ScreenFlow.Core.Room;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>
/// (1.5.0) Arquivos pelo chat: nomes seguros, o anexo dentro da mensagem e o download de verdade (TCP no próprio PC),
/// com blocos cifrados, conferência SHA-256, retomada e as recusas (sem a chave, arquivo mudou, parou de compartilhar).
/// </summary>
public sealed class FileTransferTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sf-files-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _storeKey = RandomNumberGenerator.GetBytes(32);

    public FileTransferTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ───────────────────────── Nomes ─────────────────────────

    [Theory]
    [InlineData("../../Windows/System32/evil.dll", "evil.dll")]
    [InlineData("C:\\Users\\x\\Desktop\\foto.png", "foto.png")]
    [InlineData("\\\\servidor\\pasta\\nota.txt", "nota.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("lpt1", "_lpt1")]
    [InlineData("relatorio.pdf. . .", "relatorio.pdf")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("..", "arquivo")]
    [InlineData("   ", "arquivo")]
    [InlineData(null, "arquivo")]
    public void FileNamesAreSafeToSave(string? name, string expected) => Assert.Equal(expected, FileNames.Clean(name));

    [Fact]
    public void HiddenExtensionTricksAreRemoved()
    {
        // "foto‮gpj.exe" aparece como "fotoexe.jpg" no Explorer: a marca de direção sai e a extensão de verdade fica à vista.
        var clean = FileNames.Clean("foto\u202Egpj.exe");
        Assert.Equal("fotogpj.exe", clean);
        Assert.True(FileNames.IsDangerous(clean));
        Assert.Equal("ab.txt", FileNames.Clean("a\u200Bb.txt"));
        Assert.Equal("ab.txt", FileNames.Clean("a\u0007b.txt"));
    }

    [Fact]
    public void LongNamesKeepTheExtension()
    {
        var clean = FileNames.Clean(new string('x', 300) + ".tar.gz");
        Assert.True(clean.Length <= FileNames.MaxNameLength);
        Assert.EndsWith(".gz", clean);
        var emoji = FileNames.Clean(string.Concat(Enumerable.Repeat("😀", 100)) + ".png");
        Assert.EndsWith(".png", emoji);
        Assert.False(char.IsHighSurrogate(emoji[^5])); // não corta um emoji no meio
    }

    [Fact]
    public void ProgramsAreFlaggedAndKindsDetected()
    {
        Assert.True(FileNames.IsDangerous("setup.EXE"));
        Assert.True(FileNames.IsDangerous("script.ps1"));
        Assert.True(FileNames.IsDangerous("planilha.xlsm"));
        Assert.False(FileNames.IsDangerous("fotos.zip"));
        Assert.False(FileNames.IsDangerous("video.mp4"));
        Assert.Equal(ChatFileKind.Image, FileNames.KindOf("a.JPG"));
        Assert.Equal(ChatFileKind.Video, FileNames.KindOf("a.mkv"));
        Assert.Equal(ChatFileKind.Audio, FileNames.KindOf("a.mp3"));
        Assert.Equal(ChatFileKind.File, FileNames.KindOf("a.zip"));
    }

    [Fact]
    public void UniqueNamesNeverOverwrite()
    {
        File.WriteAllText(Path.Combine(_folder, "foto.png"), "x");
        File.WriteAllText(Path.Combine(_folder, "foto (1).png"), "x");
        Assert.Equal(Path.Combine(_folder, "foto (2).png"), FileNames.Unique(_folder, "foto.png"));
        Assert.Equal(Path.Combine(_folder, "outro.png"), FileNames.Unique(_folder, "../outro.png"));
    }

    [Fact]
    public void SizesAreWrittenInPortuguese()
    {
        Assert.Equal("820 bytes", FileNames.SizeText(820));
        Assert.Equal("12 KB", FileNames.SizeText(12 * 1024));
        Assert.Equal("1,5 MB", FileNames.SizeText(1536 * 1024));
        Assert.Equal("2,00 GB", FileNames.SizeText(2L * 1024 * 1024 * 1024));
    }

    // ───────────────────────── O anexo na mensagem ─────────────────────────

    private static ChatFile SampleFile(string name = "fotos da viagem.zip", long size = 123_456_789) => new(
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), FileNames.Clean(name), size,
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        47803, FileNames.KindOf(name), 0, ["26.1.2.3", "100.64.0.9"]);

    [Fact]
    public void FileAttachmentRoundTrips()
    {
        var file = SampleFile();
        Assert.True(ChatFiles.TryDecode(ChatFiles.Encode(file), out var back));
        Assert.Equal(file.Share, back.Share);
        Assert.Equal(file.Name, back.Name);
        Assert.Equal(file.Size, back.Size);
        Assert.Equal(file.Hash, back.Hash);
        Assert.Equal(file.Key, back.Key);
        Assert.Equal(file.Port, back.Port);
        Assert.Equal(file.Kind, back.Kind);
        Assert.Equal(["26.1.2.3", "100.64.0.9"], back.Addresses ?? []);
    }

    [Fact]
    public void BrokenAttachmentsAreRejected()
    {
        var encoded = ChatFiles.Encode(SampleFile());
        Assert.False(ChatFiles.TryDecode(encoded.AsSpan(0, 40), out _));
        var huge = (byte[])encoded.Clone();
        huge[16] = 0x7F; // tamanho gigante (acima de 4 GB)
        Assert.False(ChatFiles.TryDecode(huge, out _));
        Assert.False(SampleFile() with { Port = 0 } is { IsValid: true });
        Assert.False(SampleFile() with { Name = "../x.exe" } is { IsValid: true });
    }

    [Fact]
    public void ChatMessageCarriesTheFileAndStillFits()
    {
        var file = SampleFile(new string('n', 200) + ".zip");
        var text = string.Concat(Enumerable.Repeat("ação 😀 ", 80)); // 500 caracteres com muitos bytes
        var message = new RoomChatMessage(Guid.NewGuid(), "Kayky", 42, DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000), text,
            Image: new ChatImage(new string('a', 32), 200_000, 1280, 720), Channel: "jogos", File: file);
        var packet = RoomPacket.Chat(Guid.NewGuid(), message);
        Assert.True(packet.Length <= RoomPacket.MaximumSize);
        Assert.True(RoomPacket.TryReadHeader(packet, out var type, out _));
        Assert.Equal(RoomPacketType.Chat, type);
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(packet), out var back));
        Assert.Equal(file.Share, back.File?.Share);
        Assert.Equal(file.Key, back.File?.Key);
        Assert.Equal("jogos", back.Channel);
        Assert.Equal(1280, back.Image?.Width);
        Assert.StartsWith("ação", back.Text);
    }

    [Fact]
    public void FileWithoutTextShowsAPlaceholderForOldVersions()
    {
        var file = SampleFile("notas.pdf", 2048);
        var message = new RoomChatMessage(Guid.NewGuid(), "Ana", 7, DateTimeOffset.UtcNow, string.Empty, File: file);
        var packet = RoomPacket.Chat(Guid.NewGuid(), message);
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(packet), out var back));
        Assert.Equal(file.Placeholder, back.Text);
        Assert.Contains("notas.pdf", back.Text);
        Assert.Equal(string.Empty, back.Caption); // a versão nova mostra o cartão, sem repetir o aviso
    }

    [Fact]
    public void UnknownNewFieldsAreSkipped()
    {
        var message = new RoomChatMessage(Guid.NewGuid(), "Ana", 7, DateTimeOffset.UtcNow, "oi", File: SampleFile());
        var packet = RoomPacket.Chat(Guid.NewGuid(), message).ToList();
        // Um campo de uma versão futura (tipo 99) depois do arquivo.
        packet.AddRange([99, 0, 3, 1, 2, 3]);
        Assert.True(RoomPacket.TryReadChat(RoomPacket.Body(packet.ToArray()), out var back));
        Assert.NotNull(back.File);
    }

    [Fact]
    public void DirectMessageCarriesTheFileAndFits()
    {
        var file = SampleFile(new string('d', 200) + ".7z");
        var key = RandomNumberGenerator.GetBytes(32);
        var sender = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var image = new DirectImage(new string('b', 32), 90_000, 800, 600, RandomNumberGenerator.GetBytes(32));
        var payload = new DirectPayload(string.Concat(Enumerable.Repeat("é😀", 300)), image, file);
        var body = DirectCrypto.SealMessage(key, sender, recipient, 9, DateTimeOffset.UtcNow, payload);
        Assert.True(RoomPacket.Direct(sender, body).Length <= RoomPacket.MaximumSize);
        Assert.True(DirectCrypto.TryOpenMessage(key, sender, recipient, body, out _, out _, out var back));
        Assert.Equal(file.Share, back.File?.Share);
        Assert.Equal(image.BlobId, back.Image?.BlobId);
        Assert.True(back.Text.Length > 0);
    }

    // ───────────────────────── Download de verdade ─────────────────────────

    private (FileTransferServer Server, FileShareRegistry Shares) StartServer()
    {
        var shares = new FileShareRegistry(null, null);
        var server = new FileTransferServer(shares);
        server.Start(0, IPAddress.Loopback);
        return (server, shares);
    }

    private (string Path, byte[] Data) MakeFile(int size, string name = "dados.bin")
    {
        var data = RandomNumberGenerator.GetBytes(size);
        var path = Path.Combine(_folder, "origem-" + name);
        File.WriteAllBytes(path, data);
        return (path, data);
    }

    private static async Task<ChatFile> Share(FileShareRegistry shares, string path, int port)
    {
        var hash = await FileHasher.HashAsync(path);
        var info = new FileInfo(path);
        return shares.Add(path, hash, info.Length, info.LastWriteTimeUtc).ToChatFile(port, FileNames.KindOf(path));
    }

    private FileDownload NewDownload(ChatFile file, string? destination = null)
        => new(file, destination ?? Path.Combine(_folder, "baixado-" + file.Name), Path.Combine(_folder, "estado"), _storeKey)
        {
            RetryDelay = _ => TimeSpan.FromMilliseconds(20),
        };

    private static Func<IReadOnlyList<IPEndPoint>> Loopback(int port) => () => [new IPEndPoint(IPAddress.Loopback, port)];

    [Fact]
    public async Task DownloadsAndVerifiesAFile()
    {
        var (server, shares) = StartServer();
        using var running = server;
        // 3 blocos e um pedaço: o último bloco é menor.
        var (path, data) = MakeFile(FileTransferProtocol.ChunkBytes * 3 + 12_345);
        var file = await Share(shares, path, server.Port);
        var download = NewDownload(file);
        var states = new List<FileDownloadState>();
        download.Changed += d => { lock (states) states.Add(d.State); };
        await download.StartAsync(Loopback(server.Port));
        Assert.Equal(FileDownloadState.Completed, download.State);
        Assert.Null(download.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(download.Destination));
        Assert.False(File.Exists(download.PartPath));
        Assert.False(File.Exists(FileDownload.StatePath(Path.Combine(_folder, "estado"), file.Share)));
        Assert.Contains(FileDownloadState.Verifying, states);
        Assert.Equal(file.Size, download.Received);
    }

    [Fact]
    public async Task TinyFilesWork()
    {
        var (server, shares) = StartServer();
        using var running = server;
        var (path, data) = MakeFile(1, "um-byte.txt");
        var file = await Share(shares, path, server.Port);
        var download = NewDownload(file);
        await download.StartAsync(Loopback(server.Port));
        Assert.Equal(FileDownloadState.Completed, download.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(download.Destination));
    }

    [Fact]
    public async Task WithoutTheKeyNothingComes()
    {
        var (server, shares) = StartServer();
        using var running = server;
        var (path, _) = MakeFile(50_000);
        var file = await Share(shares, path, server.Port);
        // A mesma mensagem com outra chave (alguém que não recebeu a mensagem tentando adivinhar).
        var forged = file with { Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var download = NewDownload(forged);
        await download.StartAsync(Loopback(server.Port));
        Assert.Equal(FileDownloadState.Failed, download.State);
        Assert.Contains("não compartilha mais", download.Error);
        Assert.False(File.Exists(download.Destination));
    }

    [Fact]
    public async Task StoppedSharingIsReported()
    {
        var (server, shares) = StartServer();
        using var running = server;
        var (path, _) = MakeFile(50_000);
        var file = await Share(shares, path, server.Port);
        Assert.True(shares.Remove(file.Share));
        var download = NewDownload(file);
        await download.StartAsync(Loopback(server.Port));
        Assert.Equal(FileDownloadState.Failed, download.State);
        Assert.Contains("não compartilha mais", download.Error);
    }

    [Fact]
    public async Task ChangedFileIsReported()
    {
        var (server, shares) = StartServer();
        using var running = server;
        var (path, _) = MakeFile(50_000);
        var file = await Share(shares, path, server.Port);
        await File.AppendAllTextAsync(path, "mudou");
        var download = NewDownload(file);
        await download.StartAsync(Loopback(server.Port));
        Assert.Equal(FileDownloadState.Failed, download.State);
        Assert.Contains("mudou", download.Error);
    }

    [Fact]
    public async Task ResumesFromWhereItStopped()
    {
        var (server, shares) = StartServer();
        using var running = server;
        var (path, data) = MakeFile(FileTransferProtocol.ChunkBytes * 4 + 100);
        var file = await Share(shares, path, server.Port);
        var download = NewDownload(file);
        // Dois blocos já baixados (de uma vez anterior) e o ponto guardado.
        var done = FileTransferProtocol.ChunkBytes * 2;
        await File.WriteAllBytesAsync(download.PartPath, data.AsSpan(0, done).ToArray());
        download.SaveCheckpoint(done);
        await download.StartAsync(Loopback(server.Port));
        Assert.Equal(FileDownloadState.Completed, download.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(download.Destination));
    }

    [Fact]
    public async Task ResumeOnlyTrustsWhatWasConfirmedAndTheFinalHashCatchesTheRest()
    {
        var (server, shares) = StartServer();
        using var running = server;
        var (path, data) = MakeFile(FileTransferProtocol.ChunkBytes * 3);
        var file = await Share(shares, path, server.Port);
        var download = NewDownload(file);
        // O começo guardado está errado (o disco estragou): como o download continua de onde parou, só a conferência
        // final pega, e o pedaço errado é apagado em vez de virar o arquivo.
        var wrong = RandomNumberGenerator.GetBytes(FileTransferProtocol.ChunkBytes);
        await File.WriteAllBytesAsync(download.PartPath, wrong);
        download.SaveCheckpoint(FileTransferProtocol.ChunkBytes);
        await download.StartAsync(Loopback(server.Port));
        Assert.Equal(FileDownloadState.Failed, download.State);
        Assert.Contains("SHA-256", download.Error);
        Assert.False(File.Exists(download.Destination));
        Assert.False(File.Exists(download.PartPath));
        // Baixando de novo, vem do zero e certo.
        var again = NewDownload(file);
        await again.StartAsync(Loopback(server.Port));
        Assert.Equal(FileDownloadState.Completed, again.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(again.Destination));
    }

    [Fact]
    public async Task PartialBeyondTheCheckpointIsCutBack()
    {
        var (server, shares) = StartServer();
        using var running = server;
        var (path, data) = MakeFile(FileTransferProtocol.ChunkBytes * 3);
        var file = await Share(shares, path, server.Port);
        var download = NewDownload(file);
        // O PC desligou depois de gravar um bloco e meio, mas o ponto guardado era de um bloco: o resto é descartado.
        var garbage = data.AsSpan(0, FileTransferProtocol.ChunkBytes).ToArray().Concat(RandomNumberGenerator.GetBytes(FileTransferProtocol.ChunkBytes / 2)).ToArray();
        await File.WriteAllBytesAsync(download.PartPath, garbage);
        download.SaveCheckpoint(FileTransferProtocol.ChunkBytes);
        await download.StartAsync(Loopback(server.Port));
        Assert.Equal(FileDownloadState.Completed, download.State);
        Assert.Equal(data, await File.ReadAllBytesAsync(download.Destination));
    }

    [Fact]
    public async Task StoppedDownloadComesBackPaused()
    {
        var file = SampleFile("filme.mkv", FileTransferProtocol.ChunkBytes * 10);
        var download = NewDownload(file);
        await File.WriteAllBytesAsync(download.PartPath, new byte[FileTransferProtocol.ChunkBytes]);
        download.SaveCheckpoint(FileTransferProtocol.ChunkBytes);
        var restored = FileDownload.Restore(FileDownload.StatePath(Path.Combine(_folder, "estado"), file.Share), _storeKey);
        Assert.NotNull(restored);
        Assert.Equal(FileDownloadState.Paused, restored.State);
        Assert.Equal(FileTransferProtocol.ChunkBytes, restored.Received);
        Assert.Equal(download.Destination, restored.Destination);
        // Com outra chave, o estado não abre (fica cifrado no disco).
        Assert.Null(FileDownload.Restore(FileDownload.StatePath(Path.Combine(_folder, "estado"), file.Share), RandomNumberGenerator.GetBytes(32)));
        // Cancelar apaga o pedaço e o estado.
        restored.Cancel();
        Assert.Equal(FileDownloadState.Canceled, restored.State);
        Assert.False(File.Exists(restored.PartPath));
        Assert.False(File.Exists(FileDownload.StatePath(Path.Combine(_folder, "estado"), file.Share)));
    }

    [Fact]
    public async Task NobodyAnsweringPausesWithAReason()
    {
        // Uma porta fechada no próprio PC: ninguém escutando.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var download = NewDownload(SampleFile("x.zip", 10_000));
        await download.StartAsync(Loopback(port));
        Assert.Equal(FileDownloadState.Paused, download.State);
        Assert.Contains("Não deu para falar", download.Error);
        var empty = NewDownload(SampleFile("y.zip", 10_000));
        await empty.StartAsync(() => []);
        Assert.Equal(FileDownloadState.Paused, empty.State);
        Assert.Contains("não está em nenhuma sala", empty.Error);
    }

    [Fact]
    public async Task ARequestWithTheWrongProofGetsTheSameAnswerAsAMissingFile()
    {
        var (server, shares) = StartServer();
        using var running = server;
        var (path, _) = MakeFile(10_000);
        var file = await Share(shares, path, server.Port);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        var request = FileTransferProtocol.Request(file.ShareBytes(), 0, RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32));
        await stream.WriteAsync(request);
        var response = new byte[FileTransferProtocol.ResponseBytes];
        await stream.ReadExactlyAsync(response);
        Assert.True(FileTransferProtocol.TryReadResponse(response, out var status, out var size, out _));
        Assert.Equal(FileTransferStatus.NotFound, status);
        Assert.Equal(0, size);
    }

    [Fact]
    public async Task SharesSurviveARestartButOnlyWithTheKey()
    {
        var path = Path.Combine(_folder, "compartilhados.dat");
        var key = RandomNumberGenerator.GetBytes(32);
        var (source, _) = MakeFile(1000);
        var hash = await FileHasher.HashAsync(source);
        var entry = new FileShareRegistry(path, key).Add(source, hash, 1000, File.GetLastWriteTimeUtc(source));
        Assert.Equal(entry.Key, new FileShareRegistry(path, key).Find(entry.Share)?.Key);
        Assert.Null(new FileShareRegistry(path, RandomNumberGenerator.GetBytes(32)).Find(entry.Share));
        Assert.DoesNotContain(entry.Key, Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task SharesExpire()
    {
        var now = DateTimeOffset.UtcNow;
        var shares = new FileShareRegistry(null, null, () => now);
        var (source, _) = MakeFile(1000);
        var entry = shares.Add(source, await FileHasher.HashAsync(source), 1000, File.GetLastWriteTimeUtc(source));
        Assert.NotNull(shares.Find(entry.Share));
        now += FileShareRegistry.Lifetime + TimeSpan.FromMinutes(1);
        Assert.Null(shares.Find(entry.Share));
    }

    [Fact]
    public async Task HashMatchesTheWholeFile()
    {
        var (path, data) = MakeFile(3 * 1024 * 1024 + 7);
        var progress = new List<double>();
        var hash = await FileHasher.HashAsync(path, new Progress<double>(progress.Add));
        Assert.Equal(SHA256.HashData(data), hash);
    }
}
