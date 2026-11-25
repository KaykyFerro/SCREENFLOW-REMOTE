namespace ScreenFlow.Core.Network;

/// <summary>Backoff exponencial com jitter para reconexão automática.</summary>
public sealed class Backoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _maximum;
    private readonly Random _random;

    public Backoff(TimeSpan? initial = null, TimeSpan? maximum = null, int? seed = null)
    {
        _initial = initial ?? TimeSpan.FromMilliseconds(500);
        _maximum = maximum ?? TimeSpan.FromSeconds(15);
        _random = seed is null ? new Random() : new Random(seed.Value);
    }

    public int Attempt { get; private set; }

    public TimeSpan Next()
    {
        var exponent = Math.Min(Attempt, 10);
        Attempt++;
        var baseMs = Math.Min(_maximum.TotalMilliseconds, _initial.TotalMilliseconds * Math.Pow(2, exponent));
        var jitter = 0.8 + _random.NextDouble() * 0.4; // ±20%
        return TimeSpan.FromMilliseconds(Math.Min(_maximum.TotalMilliseconds, baseMs * jitter));
    }

    public void Reset() => Attempt = 0;
}
