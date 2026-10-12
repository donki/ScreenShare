using System.Diagnostics;

namespace SocScreenShare.Recording;

/// <summary>
/// El reloj de la grabación, en unidades de 100 ns (las de Media Foundation y las que da WASAPI con
/// cada paquete de audio). Cuenta el tiempo grabado sin las pausas, y sabe colocar en esa línea de
/// tiempo un instante cualquiera del reloj del sistema: es lo que mantiene el audio a la par del
/// vídeo. Seguro entre hilos.
/// </summary>
public sealed class RecordingClock
{
    private readonly Func<long> _now;
    private readonly object _gate = new();
    private readonly List<(long Start, long End)> _pauses = [];
    private long _start = -1;
    private long _pausedAt = -1;

    /// <param name="now">El reloj del sistema en 100 ns; por defecto, el contador de alta resolución (QPC).</param>
    public RecordingClock(Func<long>? now = null) => _now = now ?? QpcNow;

    public bool IsStarted { get { lock (_gate) return _start >= 0; } }

    public bool IsPaused { get { lock (_gate) return _pausedAt >= 0; } }

    public long StartTime { get { lock (_gate) return _start; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_start >= 0)
                throw new InvalidOperationException("El reloj ya está en marcha.");
            _start = _now();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_start < 0 || _pausedAt >= 0)
                return;
            _pausedAt = _now();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_pausedAt < 0)
                return;
            _pauses.Add((_pausedAt, _now()));
            _pausedAt = -1;
        }
    }

    /// <summary>Tiempo grabado (sin pausas), en 100 ns.</summary>
    public long ElapsedTicks
    {
        get
        {
            lock (_gate)
            {
                if (_start < 0)
                    return 0;
                var end = _pausedAt >= 0 ? _pausedAt : _now();
                return end - _start - PausedBefore(end);
            }
        }
    }

    public TimeSpan Elapsed => TimeSpan.FromTicks(ElapsedTicks);

    /// <summary>
    /// Dónde cae en la grabación el instante <paramref name="systemTime"/> (100 ns del QPC).
    /// Null si es de antes de empezar o de dentro de una pausa: ese audio no se graba.
    /// </summary>
    public long? TimelineAt(long systemTime)
    {
        lock (_gate)
        {
            if (_start < 0 || systemTime < _start)
                return null;
            if (_pausedAt >= 0 && systemTime >= _pausedAt)
                return null;
            foreach (var (s, e) in _pauses)
                if (systemTime >= s && systemTime < e)
                    return null;
            return systemTime - _start - PausedBefore(systemTime);
        }
    }

    public long Now() => _now();

    private long PausedBefore(long time)
    {
        long total = 0;
        foreach (var (s, e) in _pauses)
            if (e <= time)
                total += e - s;
        return total;
    }

    /// <summary>El QPC en unidades de 100 ns, sin desbordar.</summary>
    public static long QpcNow() => ToHns(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    public static long ToHns(long timestamp, long frequency) =>
        timestamp / frequency * 10_000_000 + timestamp % frequency * 10_000_000 / frequency;
}
