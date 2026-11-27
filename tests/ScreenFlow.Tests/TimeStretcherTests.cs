using ScreenFlow.Core.Media;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>Velocidade das mensagens de voz (1.5.0): mais rápido ou mais devagar sem mudar o tom.</summary>
public class TimeStretcherTests
{
    private const int Rate = 48_000;

    private static float[] Tone(double hz, double seconds, int channels)
    {
        var frames = (int)(Rate * seconds);
        var samples = new float[frames * channels];
        for (var f = 0; f < frames; f++)
            for (var c = 0; c < channels; c++)
                samples[f * channels + c] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * f / Rate));
        return samples;
    }

    private static (float[] Output, int Position) Run(float[] clip, int channels, double speed)
    {
        var stretcher = new TimeStretcher(Rate, channels);
        var output = new List<float>();
        var position = 0;
        var chunk = new float[1024 * channels]; // como a placa de som pede: pedaços pequenos
        while (true)
        {
            var written = stretcher.Process(clip, ref position, chunk, speed);
            if (written == 0) break;
            output.AddRange(chunk.AsSpan(0, written * channels).ToArray());
            Assert.True(output.Count < clip.Length * 4, "não terminou");
        }
        return (output.ToArray(), position);
    }

    private static double Frequency(float[] samples, int channels)
    {
        var crossings = 0;
        for (var f = 1; f < samples.Length / channels; f++)
            if ((samples[(f - 1) * channels] < 0) != (samples[f * channels] < 0)) crossings++;
        return crossings / 2.0 / (samples.Length / channels / (double)Rate);
    }

    [Theory]
    [InlineData(1.5, 1)]
    [InlineData(2.0, 2)]
    [InlineData(0.75, 1)]
    public void ChangesTheLengthButNotThePitch(double speed, int channels)
    {
        var clip = Tone(300, 2, channels);
        var (output, position) = Run(clip, channels, speed);
        var expected = 2 / speed;
        var seconds = output.Length / channels / (double)Rate;
        Assert.InRange(seconds, expected * 0.95, expected * 1.05);
        Assert.InRange(Frequency(output, channels), 285, 315);
        Assert.Equal(clip.Length / channels, position);
        // As janelas somam 1: o volume não passa do original.
        Assert.True(output.Max(Math.Abs) <= 0.5f * 1.05f, $"pico {output.Max(Math.Abs)}");
    }

    [Fact]
    public void ResetStartsAgainFromTheNewPosition()
    {
        var clip = Tone(300, 1, 1);
        var stretcher = new TimeStretcher(Rate, 1);
        var position = 0;
        var chunk = new float[2048];
        stretcher.Process(clip, ref position, chunk, 1.5);
        Assert.InRange(position, 2048, 2048 * 2);
        position = Rate / 2; // pulou para a metade
        stretcher.Reset();
        stretcher.Process(clip, ref position, chunk, 1.5);
        Assert.InRange(position, Rate / 2 + 2048, Rate / 2 + 2048 * 2);
    }
}
