namespace SocScreenShare.Audio;

/// <summary>
/// El audio de un origen colocado en la línea de tiempo de la grabación (tramas estéreo a 48 kHz
/// desde el principio). Cada paquete se escribe donde le toca según su hora de captura; el
/// mezclador lee por posición y lo que no ha llegado sale como silencio. Así un origen que calla
/// (la salida del sistema no manda nada mientras no suena nada) no desplaza a los demás, y el audio
/// queda a la par del vídeo. Es un anillo: lo leído se libera y lo que llega demasiado tarde (detrás
/// de lo ya leído) o demasiado pronto (más allá de la capacidad) se descarta. Seguro entre hilos.
/// </summary>
public sealed class TimelineAudioBuffer
{
    private readonly object _gate = new();
    private readonly float[] _ring;     // estéreo intercalado
    private readonly int _capacity;     // en tramas
    private long _readPosition;         // primera trama aún no leída

    public TimelineAudioBuffer(int capacityFrames)
    {
        if (capacityFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        _capacity = capacityFrames;
        _ring = new float[capacityFrames * 2];
    }

    public long ReadPosition { get { lock (_gate) return _readPosition; } }

    /// <summary>Tramas descartadas por llegar tarde o demasiado pronto (para el registro).</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>Escribe <paramref name="stereo"/> a partir de la trama <paramref name="startFrame"/> de la grabación.</summary>
    public void Write(long startFrame, ReadOnlySpan<float> stereo)
    {
        var frames = stereo.Length / 2;
        lock (_gate)
        {
            for (var f = 0; f < frames; f++)
            {
                var pos = startFrame + f;
                if (pos < _readPosition || pos >= _readPosition + _capacity)
                {
                    DroppedFrames++;
                    continue;
                }
                var slot = (int)(pos % _capacity) * 2;
                _ring[slot] = stereo[f * 2];
                _ring[slot + 1] = stereo[f * 2 + 1];
            }
        }
    }

    /// <summary>
    /// Suma (multiplicado por <paramref name="gain"/>) las tramas desde la posición de lectura en
    /// <paramref name="mix"/>, las borra y avanza. Lo que no se escribió vale cero.
    /// </summary>
    public void ReadAdd(Span<float> mix, float gain)
    {
        var frames = mix.Length / 2;
        lock (_gate)
        {
            for (var f = 0; f < frames; f++)
            {
                var slot = (int)((_readPosition + f) % _capacity) * 2;
                mix[f * 2] += _ring[slot] * gain;
                mix[f * 2 + 1] += _ring[slot + 1] * gain;
                _ring[slot] = 0;
                _ring[slot + 1] = 0;
            }
            _readPosition += frames;
        }
    }
}

/// <summary>
/// Decide dónde va cada paquete de un origen. Si llega más o menos donde acabó el anterior (a menos
/// de <see cref="Tolerance"/>), va pegado a él, para que la pequeña imprecisión de la hora de cada
/// paquete no meta chasquidos; si no (tras un silencio de la salida, una pausa o una deriva del
/// reloj del dispositivo), se recoloca en su hora.
/// </summary>
public sealed class SourcePlacement(int toleranceFrames)
{
    private long _next = -1;

    public int Tolerance { get; } = toleranceFrames;

    /// <summary>Trama donde escribir un paquete cuya hora de captura cae en <paramref name="timestampFrame"/>.</summary>
    public long Place(long timestampFrame, int frames)
    {
        var start = _next >= 0 && Math.Abs(timestampFrame - _next) <= Tolerance ? _next : timestampFrame;
        _next = start + frames;
        return start;
    }

    /// <summary>Tras una pausa o un hueco, el siguiente paquete va a su hora.</summary>
    public void Reset() => _next = -1;
}
