using ScreenFlow.Core.Media;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>O volume do PC de quem transmite não muda o som de quem assiste (1.3.0).</summary>
public class VolumeCompensationTests
{
    private const int Channels = 2;
    private const int Frames = 480; // 10 ms a 48 kHz

    private static float[] Sine(float amplitude, int frames = Frames, int offset = 0)
    {
        var samples = new float[frames * Channels];
        for (var i = 0; i < frames; i++)
        {
            var value = amplitude * MathF.Sin(2 * MathF.PI * 440 * (i + offset) / 48_000f);
            samples[i * Channels] = value;
            samples[i * Channels + 1] = value;
        }
        return samples;
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    /// <summary>Roda alguns blocos (o ganho chega no valor certo depois do primeiro) e devolve o último.</summary>
    private static float[] Run(VolumeCompensation compensation, float amplitude, long startMs, int blocks = 5)
    {
        float[] last = [];
        for (var b = 0; b < blocks; b++)
        {
            last = Sine(amplitude, offset: b * Frames);
            compensation.Process(last, Channels, startMs + b * 10);
        }
        return last;
    }

    [Fact]
    public void LoweredVolumeIsUndone()
    {
        var compensation = new VolumeCompensation();
        compensation.SetVolume(-10f, false, 0);
        var attenuation = VolumeCompensation.Attenuation(-10f);
        // O que o app toca a 0,5 chega na captura a 0,5 × volume; quem assiste recebe de novo 0,5.
        var output = Run(compensation, 0.5f * attenuation, 0);
        Assert.Equal(0.5f, Peak(output), 2);
        Assert.False(compensation.Bypassed);
    }

    [Fact]
    public void FullVolumeMuteAndOffLeaveTheSoundAlone()
    {
        var full = new VolumeCompensation();
        full.SetVolume(0f, false, 0);
        var input = Sine(0.99f);
        var output = (float[])input.Clone();
        full.Process(output, Channels, 0);
        Assert.Equal(input, output); // nem o limitador mexe quando não há ganho

        var muted = new VolumeCompensation();
        muted.SetVolume(-20f, true, 0);
        Assert.Equal(0.05f, Peak(Run(muted, 0.05f, 0)), 3);

        var off = new VolumeCompensation { Enabled = false };
        off.SetVolume(-20f, false, 0);
        Assert.Equal(0.05f, Peak(Run(off, 0.05f, 0)), 3);

        // Sem saber o volume ainda (antes da primeira leitura), nada muda.
        var unknown = new VolumeCompensation();
        Assert.Equal(0.05f, Peak(Run(unknown, 0.05f, 0)), 3);
    }

    [Fact]
    public void PeaksAreHeldBelowTheMaximum()
    {
        var compensation = new VolumeCompensation();
        compensation.SetVolume(-10f, false, 0);
        // Um app no máximo (1,0) vira 1,0 de novo: o limitador segura um pouco abaixo do máximo.
        var output = Run(compensation, VolumeCompensation.Attenuation(-10f), 0);
        Assert.InRange(Peak(output), 0.9f, 0.9701f);
    }

    [Fact]
    public void SoundThatWasNotLoweredTurnsTheCorrectionOff()
    {
        // Volume no hardware: o volume diz −20 dB, mas o som capturado vem cheio (0,8, bem acima do teto de 0,1).
        var compensation = new VolumeCompensation();
        compensation.SetVolume(-20f, false, 0);
        Run(compensation, 0.8f, 0, blocks: 20);
        // Logo depois de mudar o volume a prova não vale (a notificação do volume chega atrasada).
        Assert.False(compensation.Bypassed);

        var output = Run(compensation, 0.8f, VolumeCompensation.SettleMs, blocks: VolumeCompensation.OverBlocksToBypass + 2);
        Assert.True(compensation.Bypassed);
        Assert.Equal(0.8f, Peak(output), 3);
    }

    [Fact]
    public void AFewOversDoNotTurnItOff()
    {
        // Dois apps somando um pouco acima do máximo, ou um estalo, não desligam a correção.
        var compensation = new VolumeCompensation();
        compensation.SetVolume(-10f, false, 0);
        var attenuation = VolumeCompensation.Attenuation(-10f);
        for (var b = 0; b < 50; b++)
        {
            var block = Sine(0.6f * attenuation, offset: b * Frames);
            if (b % 7 == 0) block[100] = attenuation * 1.4f;
            compensation.Process(block, Channels, VolumeCompensation.SettleMs + b * 10);
        }
        Assert.False(compensation.Bypassed);
        Assert.True(compensation.CurrentGain > 3f);
    }

    [Fact]
    public void GainFollowsTheVolumeWithoutJumps()
    {
        var compensation = new VolumeCompensation();
        compensation.SetVolume(-6f, false, 0);
        Run(compensation, 0.25f, 0);
        var before = compensation.CurrentGain;
        compensation.SetVolume(-12f, false, 100);
        var block = Sine(0.1f);
        compensation.Process(block, Channels, 110);
        // No bloco da mudança o ganho vai subindo amostra por amostra, até o novo valor no fim.
        Assert.Equal(1f / VolumeCompensation.Attenuation(-12f), compensation.CurrentGain, 3);
        Assert.True(compensation.CurrentGain > before);
        Assert.True(Math.Abs(block[2] / Sine(0.1f)[2] - before) < 0.5f);
    }
}
