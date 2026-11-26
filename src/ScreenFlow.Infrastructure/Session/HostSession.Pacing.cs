using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Security;
using ScreenFlow.Infrastructure.Media;

namespace ScreenFlow.Infrastructure.Session;

public sealed partial class HostSession
{
    /// <summary>
    /// Entrega um frame codificado a todos: divide em pacotes, monta a paridade que cada viewer entende (espalhada para
    /// 1.1.1, seguida para versões antigas) e põe na fila do ritmo de envio.
    /// </summary>
    private void SendFrame(MediaPacketType type, uint id, EncodedPacket packet, RemoteViewer[] audience, RetransmitCache cache)
    {
        var data = Packetizer.Split(type, id, packet.Data, packet.TimestampUs, packet.Keyframe, fecGroupSize: _options.FecGroupSize);
        cache.Store(id, data);
        List<MediaChunk>? spread = null, classic = null;
        var sends = new (RemoteViewer, List<MediaChunk>)[audience.Length];
        for (var i = 0; i < audience.Length; i++)
        {
            var viewer = audience[i];
            var chunks = viewer.SpreadFec
                ? spread ??= Packetizer.WithFec(data, _options.FecGroupSize, spread: true)
                : classic ??= Packetizer.WithFec(data, _options.FecGroupSize, spread: false);
            sends[i] = (viewer, chunks);
        }
        if (_pacer is { } pacer) pacer.EnqueueFrame(sends);
    }

    /// <summary>Ritmo para o que está indo agora: vídeo, câmera e áudio de cada viewer, com folga para FEC e cabeçalhos.</summary>
    private double PacingRate()
    {
        var watching = Math.Max(1, _viewers.Values.Count(v => v.State == ViewerState.Streaming));
        var perViewerKbps = (HasVideo ? _target.BitrateKbps : 0) + (HasCameraOverlay ? Volatile.Read(ref _cameraKbps) : 0) + _audioProfile.BitrateKbps * 2;
        return SendPacing.RateBytesPerSecond(watching * perViewerKbps * 1.25 / 1000.0, _options.UploadMbps);
    }

    /// <summary>
    /// Fila de envio com ritmo (<see cref="PacedQueue{T}"/>). Os pacotes de vídeo e da câmera saem por aqui; a
    /// retransmissão (NACK) passa na frente de tudo. O áudio vai direto: são pacotes pequenos, a cada 10 ou 20 ms, que
    /// não formam rajada.
    /// </summary>
    private sealed class Pacer : IDisposable
    {
        private const int PacketOverhead = MediaHeader.Size + PacketCipher.TagSize + 28; // + IP/UDP
        private const long MaxQueuedBytes = 48L * 1024 * 1024;

        private readonly MediaSender _sender;
        private readonly object _gate = new();
        private readonly AutoResetEvent _work = new(false);
        private readonly PacedQueue<Item> _queue;
        private readonly Thread _thread;
        private volatile bool _stopped;

        public Pacer(MediaSender sender, double bytesPerSecond)
        {
            _sender = sender;
            _queue = new PacedQueue<Item>(bytesPerSecond, MediaClock.Now);
            _thread = new Thread(Run) { IsBackground = true, Name = "ScreenFlow ritmo de envio", Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public double BytesPerSecond
        {
            get { lock (_gate) return _queue.BytesPerSecond; }
            set { lock (_gate) _queue.BytesPerSecond = value; }
        }

        /// <summary>O pacote i vai para cada viewer antes do pacote i + 1: ninguém espera o frame inteiro dos outros.</summary>
        public void EnqueueFrame(IReadOnlyList<(RemoteViewer Viewer, List<MediaChunk> Chunks)> sends)
        {
            if (sends.Count == 0) return;
            var longest = sends.Max(s => s.Chunks.Count);
            var items = new List<(Item, int)>(longest * sends.Count);
            for (var i = 0; i < longest; i++)
                foreach (var (viewer, chunks) in sends)
                    if (i < chunks.Count) items.Add((new Item(viewer, chunks[i], false), chunks[i].Payload.Length + PacketOverhead));
            int dropped;
            lock (_gate)
            {
                _queue.EnqueueBurst(items, MediaClock.Now);
                dropped = _queue.TrimTo(MaxQueuedBytes);
            }
            if (dropped > 0) Log.Warn($"Ritmo de envio: {dropped} pacotes antigos descartados (o envio travou).");
            _work.Set();
        }

        public void EnqueueRetransmission(RemoteViewer viewer, IEnumerable<MediaChunk> chunks)
        {
            lock (_gate)
            {
                var now = MediaClock.Now;
                foreach (var chunk in chunks) _queue.EnqueueUrgent(new Item(viewer, chunk, true), chunk.Payload.Length + PacketOverhead, now);
            }
            _work.Set();
        }

        private void Run()
        {
            var batch = new List<Item>(64);
            while (!_stopped)
            {
                TimeSpan wait;
                batch.Clear();
                lock (_gate)
                {
                    var now = MediaClock.Now;
                    _queue.Take(now, batch);
                    wait = _queue.Wait(now);
                }

                foreach (var item in batch)
                {
                    var viewer = item.Viewer;
                    if (viewer.State != ViewerState.Streaming) continue;
                    _sender.Send(viewer.Cipher, ref viewer.PacketCounter, item.Chunk, viewer.MediaAddress, item.Retransmit);
                }

                if (wait == Timeout.InfiniteTimeSpan) _work.WaitOne(250);
                else if (wait.TotalMilliseconds >= 1) _work.WaitOne(wait);
                else if (wait > TimeSpan.Zero) Thread.Yield();
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _work.Set();
            _thread.Join(1000);
            _work.Dispose();
        }

        private readonly record struct Item(RemoteViewer Viewer, MediaChunk Chunk, bool Retransmit);
    }
}
