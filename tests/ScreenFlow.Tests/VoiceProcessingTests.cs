using ScreenFlow.Core.Media;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>A correção do volume baixo da voz (1.5.0): AGC, compressor, limitador, nivelador, buffer e curva do mixer.</summary>
public class VoiceProcessingTests
{
    private const int Rate = 48_000;
    private const int Frame = 960; // 20 ms

    /// <summary>Uma "fala" de teste: um tom de 300 Hz com o RMS pedido (em dBFS).</summary>
    private static float[] Tone(double rmsDb, int frames, double hz = 300, int start = 0)
    {
        var amplitude = Math.Sqrt(2) * VoiceProcessor.DbToGain(rmsDb);
        var samples = new float[frames * Frame];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * (start + i) / Rate));
        return samples;
    }

    private static double RmsDb(ReadOnlySpan<float> samples) => VoiceGate.RmsDb(samples);

    [Fact]
    public void AutoGain_BringsAQuietMicrophoneUpToTheTarget()
    {
        var processor = new VoiceProcessor();
        var input = Tone(-42, 150); // 3 s de fala baixa, como um microfone comum no Windows
        for (var f = 0; f < 150; f++) processor.Process(input.AsSpan(f * Frame, Frame), speech: true);
        var lastSecond = input.AsSpan(100 * Frame, 50 * Frame);
        Assert.InRange(RmsDb(lastSecond), VoiceProcessor.TargetDb - 3, VoiceProcessor.TargetDb + 2);
        Assert.InRange(processor.AutoGainDb, 18, VoiceProcessor.MaxBoostDb);
    }

    [Fact]
    public void AutoGain_DoesNotLearnFromSilenceOrNoise()
    {
        var processor = new VoiceProcessor();
        var speech = Tone(-30, 100);
        for (var f = 0; f < 100; f++) processor.Process(speech.AsSpan(f * Frame, Frame), speech: true);
        var learned = processor.AutoGainDb;
        // Portão fechado (silêncio ou ruído): o ganho fica parado, não sobe o ruído de fundo.
        var noise = Tone(-62, 100, hz: 1200);
        for (var f = 0; f < 100; f++) processor.Process(noise.AsSpan(f * Frame, Frame), speech: false);
        Assert.Equal(learned, processor.AutoGainDb, 3);
    }

    [Fact]
    public void LoudSpeechNeverClipsAndManualGainStaysInRange()
    {
        var processor = new VoiceProcessor { ManualGainDb = 40 }; // pedido acima do máximo
        Assert.Equal(VoiceProcessor.MaxManualGainDb, processor.ManualGainDb);
        processor.ManualGainDb = 12;
        var input = Tone(-3, 100); // já perto do máximo antes do ganho: passaria muito de 0 dBFS
        for (var f = 0; f < 100; f++)
        {
            var frame = input.AsSpan(f * Frame, Frame);
            processor.ApplyManualGain(frame);
            processor.Process(frame, speech: true);
        }
        var peak = input.Max(Math.Abs);
        Assert.True(peak <= VoiceProcessor.Ceiling + 1e-4f, $"pico {peak}");
    }

    [Fact]
    public void GainChangesAreRampedWithoutClicks()
    {
        // Um tom constante e baixo: o AGC sobe uns 20 dB aos poucos. Com o ganho em rampa, duas amostras vizinhas nunca
        // pulam mais do que o próprio tom pula no volume final (um degrau de ganho seria um estalo).
        var processor = new VoiceProcessor();
        var signal = Tone(-40, 150);
        for (var f = 0; f < 150; f++) processor.Process(signal.AsSpan(f * Frame, Frame), speech: true);
        var finalPeak = signal.AsSpan(140 * Frame).ToArray().Max(Math.Abs);
        var naturalStep = 2 * Math.PI * 300 / Rate * finalPeak;
        var maxStep = 0.0;
        for (var i = 1; i < signal.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(signal[i] - signal[i - 1]));
        Assert.True(maxStep <= naturalStep * 1.05, $"degrau {maxStep:0.0000}, tom {naturalStep:0.0000}");
    }

    [Fact]
    public void ASuddenShoutAfterQuietSpeechIsCaughtAndSettles()
    {
        // Fala baixa (o AGC sobe uns 20 dB) e de repente um grito: nada passa do teto, e o grito volta ao nível normal.
        var processor = new VoiceProcessor();
        var quiet = Tone(-40, 50);
        var loud = Tone(-8, 50, start: quiet.Length);
        var signal = quiet.Concat(loud).ToArray();
        for (var f = 0; f < 100; f++) processor.Process(signal.AsSpan(f * Frame, Frame), speech: true);
        Assert.True(signal.Max(Math.Abs) <= VoiceProcessor.Ceiling + 1e-4f);
        Assert.InRange(RmsDb(signal.AsSpan(80 * Frame, 20 * Frame)), VoiceProcessor.TargetDb - 4, VoiceProcessor.TargetDb + 3);
    }

    [Fact]
    public void AutoGainOff_KeepsTheLevel()
    {
        var processor = new VoiceProcessor { AutoGain = false };
        var input = Tone(-30, 50);
        for (var f = 0; f < 50; f++) processor.Process(input.AsSpan(f * Frame, Frame), speech: true);
        Assert.InRange(RmsDb(input.AsSpan(25 * Frame)), -30.5, -29.5);
    }

    [Fact]
    public void Leveler_BoostsAQuietOldVersionAndBarelyTouchesALevelledOne()
    {
        var quiet = new VoiceLeveler();
        var input = Tone(-40, 400);
        for (var f = 0; f < 400; f++) quiet.Process(input.AsSpan(f * Frame, Frame));
        Assert.InRange(quiet.GainDb, VoiceLeveler.MaxBoostDb - 0.5, VoiceLeveler.MaxBoostDb);
        Assert.InRange(RmsDb(input.AsSpan(350 * Frame)), -29, -27);

        var levelled = new VoiceLeveler();
        var good = Tone(-20, 200);
        for (var f = 0; f < 200; f++) levelled.Process(good.AsSpan(f * Frame, Frame));
        Assert.InRange(levelled.GainDb, -1, 1);
    }

    [Fact]
    public void Limiter_HoldsTheCeilingOnStereo()
    {
        var limiter = new PeakLimiter(Rate, 0.9f, channels: 2);
        var samples = new float[Rate];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(2.2 * Math.Sin(2 * Math.PI * 440 * (i / 2) / Rate));
        limiter.Process(samples);
        Assert.True(samples.Max(Math.Abs) <= 0.9f + 1e-5f);
        Assert.True(limiter.Engaged > 0);
    }

    [Fact]
    public void Underruns_AreOnlyCountedWhenTheVoiceComesBackRightAway()
    {
        var classifier = new UnderrunClassifier();
        var t = TimeSpan.FromMinutes(1);
        // Fim de uma frase: o buffer esvazia e a próxima fala só vem 2 s depois. Não é falha da rede.
        classifier.Dry(t);
        Assert.Null(classifier.Wrote(t + TimeSpan.FromSeconds(2)));
        // Buraco da rede: o buffer esvazia e o pacote atrasado chega 40 ms depois. Isso conta.
        classifier.Dry(t + TimeSpan.FromSeconds(5));
        Assert.Equal(t + TimeSpan.FromSeconds(5), classifier.Wrote(t + TimeSpan.FromSeconds(5.04)));
        // Escrever sem ter esvaziado não conta nada.
        Assert.Null(classifier.Wrote(t + TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void GainRamp_MovesSmoothlyAndKeepsStereoTogether()
    {
        var samples = Enumerable.Repeat(1f, 200).ToArray(); // 100 quadros estéreo
        var applied = 1f;
        GainRamp.Apply(samples, ref applied, 0f, channels: 2);
        Assert.Equal(0f, applied);
        Assert.Equal(0f, samples[^1]);
        Assert.Equal(samples[0], samples[1]); // os dois lados com o mesmo ganho
        for (var i = 2; i < samples.Length; i += 2) Assert.True(samples[i - 2] - samples[i] <= 0.0101f);
        // Sem mudança de ganho e em 1: nada muda.
        var same = new[] { 0.5f, -0.5f };
        var unity = 1f;
        GainRamp.Apply(same, ref unity, 1f);
        Assert.Equal([0.5f, -0.5f], same);
    }

    [Fact]
    public void MixerCurve_FollowsTheEar()
    {
        Assert.Equal(0f, MixerCurve.Gain(0));
        Assert.Equal(1f, MixerCurve.Gain(100));
        Assert.Equal(0f, MixerCurve.Gain(80, muted: true));
        Assert.InRange(VoiceProcessor.GainToDb(MixerCurve.Gain(50)), -11, -9);
        var previous = -1f;
        for (var p = 0; p <= 100; p++)
        {
            var gain = MixerCurve.Gain(p);
            Assert.True(gain >= previous);
            previous = gain;
            // O caminho de volta (o volume antigo da voz vira a porcentagem do mixer) cai na mesma porcentagem.
            Assert.Equal(p, MixerCurve.PercentFor(gain));
        }
        Assert.Equal(100, MixerCurve.PercentFor(1.5));
        Assert.Equal(0, MixerCurve.PercentFor(double.NaN));
    }
}
