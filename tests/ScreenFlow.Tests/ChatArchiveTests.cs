using System.Text;
using ScreenFlow.Core.Room;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>O chat guardado numa pasta, como a do Google Drive para computador (1.3.0).</summary>
public sealed class ChatArchiveTests : IDisposable
{
    private static readonly Guid Ana = Guid.Parse("0d6f3a52-8b1e-4c9a-a7f2-5e3b9c1d2f42");
    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"screenflow-chat-{Guid.NewGuid():N}");

    public ChatArchiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ChatArchive Open() => new(_root, time => time.ToOffset(Brasilia).DateTime);

    private static RoomChatMessage Message(ulong id, string text, string? channel = null, ChatImage? image = null)
        => new(Ana, "Ana Lu", id, new DateTimeOffset(2026, 11, 27, 23, 5, 0, TimeSpan.Zero), text, Image: image, Channel: channel);

    [Fact]
    public void WritesTheDayAndTheHistory()
    {
        using (var archive = Open())
        {
            archive.Add(Message(1, "Boa noite!\nBora jogar?"));
            archive.Add(Message(1, "Boa noite!\nBora jogar?")); // a mesma mensagem de novo (histórico repassado)
            archive.Add(Message(2, "olha o clipe", "prints-e-clipes"));
            Assert.True(archive.Flush(TimeSpan.FromSeconds(5)));
            Assert.Null(archive.Error);
        }

        var day = File.ReadAllText(Path.Combine(_root, ChatArchive.FolderName, "geral", "2026-11-27.txt"), Encoding.UTF8);
        Assert.Contains("[20:05] Ana Lu: Boa noite!\n    Bora jogar?", day.ReplaceLineEndings("\n"));
        Assert.Single(day.Split("Ana Lu:"), part => part.Contains("Boa noite")); // gravada uma vez só
        Assert.True(File.Exists(Path.Combine(_root, ChatArchive.FolderName, "prints-e-clipes", "2026-11-27.txt")));

        using var again = Open();
        var loaded = again.LoadRecent();
        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, m => Assert.True(m.History));
        Assert.Equal("prints-e-clipes", loaded[1].Channel);
        Assert.Null(loaded[0].Channel);
        Assert.Equal("Boa noite!\nBora jogar?", loaded[0].Text);

        // O que já está guardado não é gravado de novo depois de abrir.
        again.Add(Message(2, "olha o clipe", "prints-e-clipes"));
        Assert.True(again.Flush(TimeSpan.FromSeconds(5)));
        var history = Directory.GetFiles(Path.Combine(_root, ChatArchive.FolderName, ChatArchive.HistoryFolder)).Single();
        Assert.Equal(2, File.ReadAllLines(history).Length);
    }

    [Fact]
    public void ImagesGoToTheirFolder()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 };
        var id = RoomBlob.IdOf(jpeg);
        var later = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9 };
        var laterId = RoomBlob.IdOf(later);
        using (var archive = Open())
        {
            archive.Add(Message(3, RoomPacket.ImagePlaceholder, image: new ChatImage(id, jpeg.Length, 10, 10)), jpeg);
            archive.Add(Message(4, "e essa", image: new ChatImage(laterId, later.Length, 10, 10)));
            archive.AddImage(laterId, later); // chegou depois da mensagem
            Assert.True(archive.Flush(TimeSpan.FromSeconds(5)));
        }
        var images = Path.Combine(_root, ChatArchive.FolderName, ChatArchive.ImagesFolder);
        Assert.Equal(jpeg, File.ReadAllBytes(Path.Combine(images, id + ".jpg")));
        Assert.Equal(later, File.ReadAllBytes(Path.Combine(images, laterId + ".png")));
        var day = File.ReadAllText(Path.Combine(_root, ChatArchive.FolderName, "geral", "2026-11-27.txt"));
        Assert.Contains($"Ana Lu: [imagem: ../imagens/{id}.jpg]", day);
        Assert.DoesNotContain(RoomPacket.ImagePlaceholder, day);

        // As mensagens antigas pegam a imagem de volta da pasta (quando ela já saiu deste PC).
        using var again = Open();
        Assert.Equal(jpeg, again.ReadImage(id));
        Assert.Equal(later, again.ReadImage(laterId));
        Assert.Null(again.ReadImage(RoomBlob.IdOf([1, 2, 3])));
        Assert.Null(again.ReadImage("../../fora"));
        File.WriteAllBytes(Path.Combine(images, id + ".jpg"), [0xFF, 0xD8, 0xFF, 9]); // trocada: não confere com o id
        Assert.Null(again.ReadImage(id));
    }

    [Fact]
    public void BrokenLinesAreSkipped()
    {
        var folder = Path.Combine(_root, ChatArchive.FolderName, ChatArchive.HistoryFolder);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "2026-11.jsonl"),
            "não é json\n{\"a\":\"00000000-0000-0000-0000-000000000000\",\"n\":\"x\",\"i\":1,\"t\":1,\"x\":\"oi\"}\n" +
            $"{{\"a\":\"{Ana}\",\"n\":\"Ana\",\"i\":5,\"t\":1764000000000,\"x\":\"certo\",\"c\":\"geral\"}}\n");
        using var archive = Open();
        var loaded = Assert.Single(archive.LoadRecent());
        Assert.Equal("certo", loaded.Text);
        Assert.Null(loaded.Channel);
    }

    [Fact]
    public void ChannelNamesAreSafeFolders()
    {
        Assert.Equal("a-b-c", ChatArchive.SafeName("a/b\\c"));
        Assert.Equal("geral", ChatArchive.SafeName(" .. "));
        Assert.Equal("música", ChatArchive.SafeName("música"));
    }

    [Fact]
    public void FindsTheGoogleDriveFolder()
    {
        var drive = Path.Combine(_root, "G");
        Directory.CreateDirectory(Path.Combine(drive, ".shortcut-targets-by-id"));
        Directory.CreateDirectory(Path.Combine(drive, "Meu Drive"));
        Assert.Equal(Path.Combine(drive, "Meu Drive"), CloudFolders.FindGoogleDrive([(_root, "Disco local"), (drive, "Google Drive")], null));

        // Modo espelhado: a pasta fica dentro do usuário.
        var profile = Path.Combine(_root, "Users", "kayky");
        Directory.CreateDirectory(Path.Combine(profile, "My Drive"));
        Assert.Equal(Path.Combine(profile, "My Drive"), CloudFolders.FindGoogleDrive([(_root, "Disco local")], profile));
        Assert.Null(CloudFolders.FindGoogleDrive([(_root, null)], Path.Combine(_root, "ninguém")));
    }

    [Fact]
    public void ChosenScreenFlowChatFolderIsNotNestedAgain()
    {
        var drive = Path.Combine(Path.GetTempPath(), "Meu Drive");
        Assert.Equal(drive, ChatArchive.RootFor(drive));
        Assert.Equal(drive, ChatArchive.RootFor(Path.Combine(drive, ChatArchive.FolderName)));
        Assert.Equal(drive, ChatArchive.RootFor(Path.Combine(drive, "screenflow chat") + Path.DirectorySeparatorChar));
        var other = Path.Combine(drive, "Conversas");
        Assert.Equal(other, ChatArchive.RootFor(other));
        using var archive = new ChatArchive(ChatArchive.RootFor(Path.Combine(_root, ChatArchive.FolderName)));
        Assert.Equal(Path.Combine(_root, ChatArchive.FolderName), archive.Folder);
    }
}
