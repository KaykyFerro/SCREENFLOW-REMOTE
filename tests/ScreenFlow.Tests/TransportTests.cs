using System.Security.Cryptography;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.Tests;

/// <summary>Transporte da 1.1.1: FEC espalhado, NACK repetido e de frame inteiro, pulo para keyframe e ritmo de envio.</summary>
public class TransportTests(ITestOutputHelper output)
{
    private static byte[] RandomFrame(int size) => RandomNumberGenerator.GetBytes(size);

    private static List<AssembledFrame> Feed(FrameReassembler reassembler, IEnumerable<MediaChunk> chunks, double ms)
    {
        var ready = new List<AssembledFrame>();
        foreach (var chunk in chunks) ready.AddRange(reassembler.Add(chunk.Header, chunk.Payload.Span, TimeSpan.FromMilliseconds(ms)));
        return ready;
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(12, 10)]
    [InlineData(95, 10)]
    [InlineData(23, 6)]
    [InlineData(300, 10)]
    public void FecGroups_CoverEveryPacketOnce(int count, int groupSize)
    {
        foreach (var spread in new[] { false, true })
        {
            var seen = new int[count];
            for (var g = 0; g < Packetizer.FecGroups(count, groupSize); g++)
                foreach (var i in Packetizer.GroupMembers(g, count, groupSize, spread)) seen[i]++;
            Assert.All(seen, n => Assert.Equal(1, n));
        }
    }

    [Fact]
    public void SpreadFec_RecoversBurstThatClassicCannot()
    {
        var frame = RandomFrame(95 * Packetizer.DefaultPayloadSize - 300); // 95 pacotes, 10 grupos
        var lost = Enumerable.Range(40, 6).ToHashSet(); // 6 pacotes seguidos

        foreach (var spread in new[] { false, true })
        {
            var chunks = Packetizer.Packetize(MediaPacketType.Video, 3, frame, 0, true, fecGroupSize: 10, spreadFec: spread);
            Assert.All(chunks.Where(c => c.Header.Type == MediaPacketType.VideoFec), c => Assert.Equal(spread, c.Header.Flags.HasFlag(MediaPacketFlags.SpreadFec)));
            var reassembler = new FrameReassembler();
            var ready = Feed(reassembler, chunks.Where(c => !(c.Header.Type == MediaPacketType.Video && lost.Contains(c.Header.Index))), 0);
            if (spread)
            {
                Assert.Equal(frame, Assert.Single(ready).Data);
                Assert.Equal(6, reassembler.PacketsRecoveredByFec);
            }
            else
            {
                Assert.Empty(ready); // 6 perdas no mesmo grupo: só o NACK resolve
            }
        }
    }

    [Fact]
    public void Parity_GoesBetweenDataPackets()
    {
        var chunks = Packetizer.Packetize(MediaPacketType.Video, 1, RandomFrame(30 * 1150), 0, false, fecGroupSize: 10, spreadFec: true);
        var types = chunks.Select(c => c.Header.Type == MediaPacketType.VideoFec ? 'P' : 'D').ToArray();
        Assert.Equal(new string('D', 10) + "P" + new string('D', 10) + "P" + new string('D', 10) + "P", new string(types));
    }

    [Fact]
    public void Nack_WaitsUntilFrameStopsArriving()
    {
        var chunks = Packetizer.Packetize(MediaPacketType.Video, 1, RandomFrame(60 * 1150), 0, false, fecGroupSize: 0);
        var reassembler = new FrameReassembler(TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(300));
        // Com o ritmo de envio, um pacote a cada 0,5 ms: o frame leva 30 ms para chegar. O pacote 5 se perdeu.
        for (var i = 0; i < chunks.Count; i++)
        {
            if (i == 5) continue;
            reassembler.Add(chunks[i].Header, chunks[i].Payload.Span, TimeSpan.FromMilliseconds(i * 0.5));
            var (early, _, _) = reassembler.Tick(TimeSpan.FromMilliseconds(i * 0.5));
            Assert.Empty(early); // o frame ainda está chegando: não pede o que pode estar a caminho
        }
        var (nacks, _, _) = reassembler.Tick(TimeSpan.FromMilliseconds(29.5 + 8));
        Assert.Equal(new ushort[] { 5 }, Assert.Single(nacks).Missing);
    }

    [Fact]
    public void Nack_RepeatsWhenRetransmissionIsLost()
    {
        var chunks = Packetizer.Packetize(MediaPacketType.Video, 1, RandomFrame(5_000), 0, false, fecGroupSize: 0);
        var reassembler = new FrameReassembler(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(200))
        {
            NackRetryInterval = TimeSpan.FromMilliseconds(30),
            MaxNacksPerFrame = 3,
        };
        Feed(reassembler, chunks.Where(c => c.Header.Index != 2), 0);

        Assert.Single(reassembler.Tick(TimeSpan.FromMilliseconds(10)).Nacks);
        Assert.Empty(reassembler.Tick(TimeSpan.FromMilliseconds(20)).Nacks); // ainda esperando a resposta
        Assert.Single(reassembler.Tick(TimeSpan.FromMilliseconds(41)).Nacks); // a retransmissão também se perdeu
        Assert.Single(reassembler.Tick(TimeSpan.FromMilliseconds(72)).Nacks);
        Assert.Empty(reassembler.Tick(TimeSpan.FromMilliseconds(103)).Nacks); // no máximo 3 vezes
        Assert.Equal(3, reassembler.NacksSent);

        var retransmitted = chunks.First(c => c.Header.Index == 2);
        var header = retransmitted.Header;
        header.Flags |= MediaPacketFlags.Retransmit;
        var ready = reassembler.Add(header, retransmitted.Payload.Span, TimeSpan.FromMilliseconds(110));
        Assert.Single(ready);
        Assert.Equal(1, reassembler.PacketsRecoveredByNack);
    }

    [Fact]
    public void WholeFrameNack_AsksForFrameThatNeverArrived()
    {
        var first = Packetizer.Packetize(MediaPacketType.Video, 1, RandomFrame(3_000), 0, false, fecGroupSize: 0);
        var third = Packetizer.Packetize(MediaPacketType.Video, 3, RandomFrame(3_000), 0, false, fecGroupSize: 0);
        var reassembler = new FrameReassembler(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(100)) { WholeFrameNack = true };
        Assert.Single(Feed(reassembler, first, 0));
        Assert.Empty(Feed(reassembler, third, 1)); // o frame 2 sumiu inteiro

        var (nacks, ready, dropped) = reassembler.Tick(TimeSpan.FromMilliseconds(7));
        var nack = Assert.Single(nacks);
        Assert.Equal(2u, nack.FrameId);
        Assert.Empty(nack.Missing);
        Assert.Empty(ready);
        Assert.Equal(0, dropped);

        // O host reenvia o frame inteiro (NACK sem índices).
        var cache = new RetransmitCache();
        var second = Packetizer.Packetize(MediaPacketType.Video, 2, RandomFrame(3_000), 0, false, fecGroupSize: 10);
        cache.Store(2, second);
        var resent = cache.Lookup(2, nack.Missing);
        Assert.Equal(second.Count(c => c.Header.Type == MediaPacketType.Video), resent.Count);
        var delivered = Feed(reassembler, resent, 30);
        Assert.Equal(new uint[] { 2, 3 }, delivered.Select(f => f.FrameId));
        Assert.Equal(0, reassembler.FramesDropped);
    }

    [Fact]
    public void MissingFrame_IsSkippedAtDeadlineAndCounted()
    {
        var first = Packetizer.Packetize(MediaPacketType.Video, 1, RandomFrame(3_000), 0, false, fecGroupSize: 0);
        var fourth = Packetizer.Packetize(MediaPacketType.Video, 4, RandomFrame(3_000), 0, false, fecGroupSize: 0);
        var reassembler = new FrameReassembler(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(100)) { WholeFrameNack = true };
        Feed(reassembler, first, 0);
        Feed(reassembler, fourth, 1);
        Assert.Empty(reassembler.Tick(TimeSpan.FromMilliseconds(80)).Ready); // ainda no prazo
        var (_, ready, dropped) = reassembler.Tick(TimeSpan.FromMilliseconds(102));
        Assert.Equal(2, dropped);
        Assert.Equal(4u, Assert.Single(ready).FrameId);
        Assert.True(ready[0].AfterGap);
        Assert.Equal(2, reassembler.FramesMissing);
        Assert.Equal(0, reassembler.FramesExpired);
    }

    [Fact]
    public void CompleteKeyframe_ReleasesFramesStuckBehindIncompleteOne()
    {
        var stuck = Packetizer.Packetize(MediaPacketType.Video, 1, RandomFrame(3_000), 0, false, fecGroupSize: 0);
        var behind = Packetizer.Packetize(MediaPacketType.Video, 2, RandomFrame(3_000), 0, false, fecGroupSize: 0);
        var keyframe = Packetizer.Packetize(MediaPacketType.Video, 3, RandomFrame(20_000), 0, true, fecGroupSize: 0);
        var reassembler = new FrameReassembler(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(120));
        Feed(reassembler, stuck.Skip(1), 0); // frame 1 sem o primeiro pacote
        Assert.Empty(Feed(reassembler, behind, 1));
        var ready = Feed(reassembler, keyframe, 2);
        var frame = Assert.Single(ready); // não espera os 120 ms do frame 1
        Assert.Equal(3u, frame.FrameId);
        Assert.True(frame.Keyframe);
        Assert.False(frame.AfterGap); // keyframe não precisa pedir outro keyframe
        Assert.Equal(2, reassembler.FramesSuperseded);
    }

    [Fact]
    public void PendingInput_DoublesDeadline()
    {
        var first = Packetizer.Packetize(MediaPacketType.Video, 1, RandomFrame(3_000), 0, false, fecGroupSize: 0);
        var reassembler = new FrameReassembler(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(50));
        Feed(reassembler, first.Take(1), 0);
        Assert.Equal(0, reassembler.Tick(TimeSpan.FromMilliseconds(60), inputPending: true).Dropped);
        Assert.Equal(1, reassembler.Tick(TimeSpan.FromMilliseconds(60)).Dropped);
    }

    private static List<(double Ms, int Item)> Drain(PacedQueue<int> queue, double startMs, double untilMs)
    {
        var sent = new List<(double, int)>();
        var batch = new List<int>();
        for (var ms = startMs; ms <= untilMs && queue.Count > 0; ms += 0.5)
        {
            batch.Clear();
            queue.Take(TimeSpan.FromMilliseconds(ms), batch);
            sent.AddRange(batch.Select(item => (ms, item)));
        }
        return sent;
    }

    [Fact]
    public void PacedQueue_SpreadsSmallFrameAtTheRate()
    {
        var queue = new PacedQueue<int>(SendPacing.RateBytesPerSecond(3.3), TimeSpan.Zero); // 30 Mbps
        queue.EnqueueBurst(Enumerable.Range(0, 12).Select(i => (i, 1_200)), TimeSpan.Zero);
        var sent = Drain(queue, 0, 100);
        Assert.Equal(Enumerable.Range(0, 12), sent.Select(s => s.Item));
        // 14 KB a 30 Mbps: ~4 ms, e não tudo no mesmo instante (o que sai junto é no máximo 2 ms do ritmo).
        Assert.InRange(sent[^1].Ms, 2, 6);
        Assert.True(sent.GroupBy(s => s.Ms).Max(g => g.Count()) <= 7);
    }

    [Fact]
    public void PacedQueue_BigFrameSpeedsUpToFinishInMaxDelay()
    {
        // 120 KB (um keyframe) com ritmo de 8 Mbps levaria 120 ms: o prazo de 50 ms acelera, sem mandar tudo de uma vez.
        var queue = new PacedQueue<int>(1_000_000, TimeSpan.Zero);
        queue.EnqueueBurst(Enumerable.Range(0, 100).Select(i => (i, 1_200)), TimeSpan.Zero);
        var sent = Drain(queue, 0, 200);
        Assert.Equal(100, sent.Count);
        Assert.InRange(sent[^1].Ms, 40, 55);
        Assert.True(sent.GroupBy(s => Math.Floor(s.Ms)).Max(g => g.Count()) <= 5, "no máximo uns poucos pacotes por milissegundo");
    }

    [Fact]
    public void PacedQueue_RetransmissionGoesFirst()
    {
        var queue = new PacedQueue<int>(1_000_000, TimeSpan.Zero);
        queue.EnqueueBurst(Enumerable.Range(0, 50).Select(i => (i, 1_200)), TimeSpan.Zero);
        var first = new List<int>();
        queue.Take(TimeSpan.Zero, first);
        queue.EnqueueUrgent(-1, 1_200, TimeSpan.FromMilliseconds(1));
        var next = new List<int>();
        queue.Take(TimeSpan.FromMilliseconds(2), next);
        Assert.Equal(-1, next[0]);
        Assert.Equal(51, first.Count + Drain(queue, 2, 200).Count + next.Count);
    }

    [Fact]
    public void PacingRate_HasFloorHeadroomAndUploadCap()
    {
        Assert.Equal(SendPacing.FloorMbps * 1_000_000 / 8, SendPacing.RateBytesPerSecond(3.3));
        Assert.Equal(120.0 * 3 * 1_000_000 / 8, SendPacing.RateBytesPerSecond(120));
        // Upload de 50 Mbps: até 45 Mbps, mas nunca abaixo de 1,5 vez a média.
        Assert.Equal(45.0 * 1_000_000 / 8, SendPacing.RateBytesPerSecond(20, 50));
        Assert.Equal(60.0 * 1_000_000 / 8, SendPacing.RateBytesPerSecond(40, 50));
    }

    [Fact]
    public void Adaptive_StaticScreenIsNotASlowViewer()
    {
        var profile = new StreamTarget(12_000, 60, Resolution.P1080);
        var controller = new AdaptiveBitrateController(profile, VideoCodec.Hevc);
        // Tela parada ou vídeo de 30 FPS: o host manda 10 a 30 frames por segundo e o viewer decodifica todos.
        for (var i = 0; i < 20; i++) Assert.Null(controller.Update(new NetworkSample(0, 20, 5_000, i % 2 == 0 ? 10 : 29, i % 2 == 0 ? 10 : 30)));
        Assert.Equal(profile, controller.Current);
        // Viewer lento de verdade: recebe 60 e decodifica 30, por dois segundos seguidos.
        Assert.Null(controller.Update(new NetworkSample(0, 20, 12_000, 30, 60)));
        Assert.NotNull(controller.Update(new NetworkSample(0, 20, 12_000, 30, 60)));
    }

    // ───────────────────────── Simulação de rede com perdas em rajada ─────────────────────────

    private sealed record SimulationResult(long Frames, long Dropped, long FecRecovered, long NackRecovered, long Keyframes);

    /// <summary>
    /// 1.000 frames a 60 FPS por um link com perdas em rajada (modelo de Gilbert-Elliott, ~2% de perda, rajadas de ~3
    /// pacotes, como um Wi-Fi ruim), RTT de 22 ms. Compara o jeito antigo (FEC de pacotes seguidos, um NACK por frame,
    /// frame que some inteiro é pulado) com o da 1.1.1.
    /// </summary>
    private static SimulationResult Simulate(bool improved, int seed)
    {
        var random = new Random(seed);
        var events = new PriorityQueue<Action, (double, long)>();
        long order = 0;
        double now = 0;
        void At(double time, Action action) => events.Enqueue(action, (time, order++));

        var reassembler = new FrameReassembler(TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(120))
        {
            WholeFrameNack = improved,
            MaxNacksPerFrame = improved ? 3 : 1,
            NackRetryInterval = TimeSpan.FromMilliseconds(1.25 * 22 + 5),
        };
        var cache = new RetransmitCache(256);
        var bad = false;
        bool Lost()
        {
            bad = bad ? random.NextDouble() >= 0.3 : random.NextDouble() < 0.01;
            return random.NextDouble() < (bad ? 0.6 : 0.002);
        }

        const double oneWayMs = 11;
        const double packetMs = 0.32; // ~30 Mbps no gargalo
        double linkFree = 0;
        void Send(MediaChunk chunk, bool retransmit)
        {
            var header = chunk.Header;
            if (retransmit) header.Flags |= MediaPacketFlags.Retransmit;
            var leave = Math.Max(now, linkFree);
            linkFree = leave + packetMs;
            if (Lost()) return;
            var payload = chunk.Payload.ToArray();
            At(leave + oneWayMs, () => reassembler.Add(header, payload, TimeSpan.FromMilliseconds(now)));
        }

        var keyframeRequested = true;
        double lastKeyframeRequest = -1_000;
        long keyframes = 0;
        void RequestKeyframe()
        {
            if (now - lastKeyframeRequest < 300) return;
            lastKeyframeRequest = now;
            At(now + oneWayMs, () => keyframeRequested = true);
        }

        const int frames = 1_000;
        for (var i = 0; i < frames; i++)
        {
            var id = (uint)i;
            At(i * 1000.0 / 60, () =>
            {
                var keyframe = keyframeRequested || id % 300 == 0;
                if (keyframe) { keyframeRequested = false; keyframes++; }
                var size = keyframe ? 120_000 : 10_000 + random.Next(8_000);
                var data = Packetizer.Split(MediaPacketType.Video, id, new byte[size], 0, keyframe, fecGroupSize: 10);
                cache.Store(id, data);
                foreach (var chunk in Packetizer.WithFec(data, 10, improved)) Send(chunk, false);
            });
        }
        for (var t = 0.0; t < frames * 1000.0 / 60 + 1_000; t += 4)
        {
            At(t, () =>
            {
                var (nacks, ready, dropped) = reassembler.Tick(TimeSpan.FromMilliseconds(now));
                if (dropped > 0 || ready.Any(f => f.AfterGap)) RequestKeyframe();
                foreach (var nack in nacks)
                    At(now + oneWayMs, () =>
                    {
                        foreach (var chunk in cache.Lookup(nack.FrameId, nack.Missing)) Send(chunk, true);
                    });
            });
        }

        while (events.TryDequeue(out var action, out var key))
        {
            now = key.Item1;
            action();
        }
        return new SimulationResult(reassembler.FramesCompleted + reassembler.FramesDropped, reassembler.FramesDropped,
            reassembler.PacketsRecoveredByFec, reassembler.PacketsRecoveredByNack, keyframes);
    }

    [Fact]
    public void BurstLoss_NewTransportLosesFarFewerFrames()
    {
        long oldDropped = 0, newDropped = 0;
        for (var seed = 1; seed <= 3; seed++)
        {
            var before = Simulate(improved: false, seed);
            var after = Simulate(improved: true, seed);
            output.WriteLine($"semente {seed}: antes {before}, depois {after}");
            oldDropped += before.Dropped;
            newDropped += after.Dropped;
            Assert.True(after.FecRecovered > before.FecRecovered, "o FEC espalhado recupera mais pacotes");
        }
        output.WriteLine($"frames perdidos: antes {oldDropped}, depois {newDropped}");
        Assert.True(newDropped * 4 <= oldDropped, $"esperava pelo menos 4 vezes menos frames perdidos (antes {oldDropped}, depois {newDropped})");
    }
}

/// <summary>Atraso do som (1.1.1): o buffer começa no mínimo e só cresce quando o som falha.</summary>
public class AudioPlayoutTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void StartsAtMinimumAndGrowsOnlyOnUnderruns()
    {
        var playout = new PlayoutController(25, 90);
        Assert.Equal(25, playout.TargetMs);
        playout.Underrun(Ms(1_000));
        playout.Underrun(Ms(1_100));
        Assert.Equal(45, playout.TargetMs);
        for (var i = 0; i < 20; i++) playout.Underrun(Ms(2_000 + i));
        Assert.Equal(90, playout.TargetMs); // nunca passa do teto
    }

    [Fact]
    public void ShrinksBackAfterCalmNetwork()
    {
        var playout = new PlayoutController(25, 90);
        playout.Underrun(Ms(0));
        playout.Underrun(Ms(10));
        Assert.Equal(45, playout.TargetMs);
        playout.Update(45, Ms(10_000));
        Assert.Equal(45, playout.TargetMs); // ainda não passaram 15 s sem falha
        for (var t = 15_100.0; t < 60_000; t += 100) playout.Update(playout.TargetMs, Ms(t));
        Assert.Equal(25, playout.TargetMs);
    }

    [Fact]
    public void ReadsFasterAboveTargetAndSlowerBelow()
    {
        var above = new PlayoutController(25, 90);
        var rate = 1.0;
        for (var t = 0; t < 200; t++) rate = above.Update(60, Ms(t * 10));
        Assert.True(rate > 1.0 && rate <= 1.01, $"acima do alvo lê mais rápido ({rate})");

        var below = new PlayoutController(40, 160);
        for (var t = 0; t < 200; t++) rate = below.Update(20, Ms(t * 10));
        Assert.True(rate < 1.0 && rate >= 0.99, $"abaixo do alvo lê mais devagar ({rate})");

        var onTarget = new PlayoutController(25, 90);
        for (var t = 0; t < 200; t++) rate = onTarget.Update(t % 2 == 0 ? 20 : 30, Ms(t * 10));
        Assert.Equal(1.0, rate); // o sobe e desce de cada pacote não conta como atraso
    }

    [Fact]
    public void HardLimitStaysCloseToTarget()
    {
        var playout = new PlayoutController(25, 90);
        Assert.True(playout.HardLimitMs <= 120);
        Assert.True(new PlayoutController(40, 160).HardLimitMs <= 160);
    }
}
