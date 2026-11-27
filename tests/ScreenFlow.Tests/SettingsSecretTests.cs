using System.Text;
using ScreenFlow.Core.Room;
using ScreenFlow.Core.Security;
using ScreenFlow.Core.Settings;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>
/// (1.5.0) Senhas, PIN e convite nunca mais em texto puro nas configurações, e a atualização da 1.4.1 (o "volume de quem
/// fala" vira o volume da voz no mixer). Os testes que mexem no protetor (global) rodam um de cada vez, na mesma coleção.
/// </summary>
[Collection("Segredos")]
public class SettingsSecretTests
{
    /// <summary>Um protetor de teste (no app é o DPAPI do Windows): inverte e embaralha os bytes.</summary>
    private sealed class TestProtector : ISecretProtector
    {
        public string Scheme => "teste";
        public byte[] Protect(byte[] data) => data.Reverse().Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).Reverse().ToArray();
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"screenflow-secrets-{Guid.NewGuid():N}.json");

    [Fact]
    public void OldPlainSecretsAreReadOnceAndSavedProtected()
    {
        SecretProtection.Register(new TestProtector());
        var invite = RoomInvite.Create(Guid.NewGuid()).ToCode();
        var path = TempFile();
        try
        {
            // Como a 1.4.1 gravava: tudo em texto puro, e o volume da voz em 50%.
            File.WriteAllText(path, $$"""
                { "SettingsVersion": 2, "Pin": "4321", "ServerPassword": "senha do servidor",
                  "HostedServerPassword": "minha senha", "PrivateRoomCode": "{{invite}}", "VoiceVolume": 0.5 }
                """);
            var store = new SettingsStore(path);
            var settings = store.Current;
            Assert.Equal("4321", settings.Pin);
            Assert.Equal("senha do servidor", settings.ServerPassword);
            Assert.Equal("minha senha", settings.HostedServerPassword);
            Assert.Equal(invite, settings.PrivateRoomCode);
            Assert.Equal(AppSettings.CurrentSettingsVersion, settings.SettingsVersion);
            // 0,5 de ganho na curva nova: soa igual ao que a pessoa tinha.
            Assert.Equal(66, settings.Mixer.VoipPercent);
            Assert.Equal(100, settings.Mixer.StreamsPercent);

            store.Save();
            var text = File.ReadAllText(path, Encoding.UTF8);
            Assert.DoesNotContain("4321", text);
            Assert.DoesNotContain("senha do servidor", text);
            Assert.DoesNotContain("minha senha", text);
            Assert.DoesNotContain(invite, text);
            Assert.DoesNotContain("\"VoiceVolume\"", text);
            Assert.DoesNotContain("\"ServerPassword\"", text);
            Assert.Contains("\"ServerPasswordProtected\": \"teste:", text);

            var reloaded = new SettingsStore(path).Current;
            Assert.Equal("4321", reloaded.Pin);
            Assert.Equal("senha do servidor", reloaded.ServerPassword);
            Assert.Equal("minha senha", reloaded.HostedServerPassword);
            Assert.Equal(invite, reloaded.PrivateRoomCode);
            Assert.Equal(66, reloaded.Mixer.VoipPercent); // a migração não roda de novo
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WithoutAProtectorSecretsAreNotWrittenAndForeignOnesDoNotOpen()
    {
        var path = TempFile();
        try
        {
            SecretProtection.Register(null);
            var store = new SettingsStore(path);
            store.Current.ServerPassword = "segredo";
            store.Current.Pin = "9876";
            store.Save();
            var text = File.ReadAllText(path, Encoding.UTF8);
            Assert.DoesNotContain("segredo", text);
            Assert.DoesNotContain("9876", text);
            // Ainda vale enquanto o app está aberto.
            Assert.Equal("segredo", store.Current.ServerPassword);

            // Um segredo cifrado de outro jeito (outro PC, outra conta) não abre e não derruba as configurações.
            SecretProtection.Register(new TestProtector());
            File.WriteAllText(path, """{ "SettingsVersion": 3, "ServerPasswordProtected": "dpapi:AAAA", "DeviceName": "PC" }""");
            var foreign = new SettingsStore(path).Current;
            Assert.Null(foreign.ServerPassword);
            Assert.Equal("PC", foreign.DeviceName);
        }
        finally
        {
            SecretProtection.Register(new TestProtector());
            File.Delete(path);
        }
    }

    [Fact]
    public void MixerAndMicrophoneValuesAreCleaned()
    {
        var settings = new AppSettings
        {
            Mixer = new AudioMixerSettings { VoipPercent = 140, StreamsPercent = -5, AppSoundsPercent = 55 },
            MicGainDb = 99,
        };
        settings.Normalize();
        Assert.Equal(100, settings.Mixer.VoipPercent);
        Assert.Equal(0, settings.Mixer.StreamsPercent);
        Assert.Equal(55, settings.Mixer.AppSoundsPercent);
        Assert.Equal(24, settings.MicGainDb);

        settings.Mixer = null!;
        settings.MicGainDb = double.NaN;
        settings.Normalize();
        Assert.Equal(100, settings.Mixer.VoipPercent);
        Assert.Equal(0, settings.MicGainDb);
    }
}
