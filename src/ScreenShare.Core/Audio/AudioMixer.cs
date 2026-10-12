namespace SocScreenShare.Audio;

/// <summary>Un origen de la mezcla: su línea de tiempo, su volumen y si está activo.</summary>
public sealed class MixerInput(string name, TimelineAudioBuffer buffer)
{
    private float _gain = 1f;

    public string Name { get; } = name;
    public TimelineAudioBuffer Buffer { get; } = buffer;
    public bool Enabled { get; set; } = true;

    /// <summary>Volumen de 0 a 2 (0 % a 200 %).</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 0f, 2f);
    }
}

/// <summary>
/// Mezcla los orígenes elegidos (salida del sistema o una aplicación, y el micrófono) en un solo
/// flujo estéreo a la frecuencia de salida (48 o 44,1 kHz), con el volumen de cada uno, y lo entrega por tramos al ritmo del reloj de
/// la grabación, con un margen (<see cref="LatencyFrames"/>) para que lleguen los paquetes que
/// Windows entrega con algo de retraso. Lo que pase de ±1 se recorta.
/// </summary>
public sealed class AudioMixer
{
    public const int DefaultSampleRate = 48000;

    private readonly List<MixerInput> _inputs = [];

    /// <param name="latencyFrames">Margen para los paquetes que llegan tarde; por defecto, 150 ms.</param>
    public AudioMixer(int sampleRate = DefaultSampleRate, int? latencyFrames = null)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
        LatencyFrames = latencyFrames ?? sampleRate * 150 / 1000;
    }

    public int SampleRate { get; }

    public int LatencyFrames { get; }

    /// <summary>Tramas ya entregadas al codificador.</summary>
    public long WrittenFrames { get; private set; }

    public IReadOnlyList<MixerInput> Inputs => _inputs;

    public MixerInput Add(string name, int? capacityFrames = null)
    {
        var input = new MixerInput(name, new TimelineAudioBuffer(capacityFrames ?? SampleRate * 4));
        _inputs.Add(input);
        return input;
    }

    /// <summary>Tramas que ya se pueden mezclar cuando la grabación lleva <paramref name="elapsedFrames"/>.</summary>
    public long FramesDue(long elapsedFrames, bool final = false) =>
        Math.Max(0, elapsedFrames - (final ? 0 : LatencyFrames) - WrittenFrames);

    /// <summary>Mezcla las siguientes <c>mix.Length / 2</c> tramas en <paramref name="mix"/> y avanza.</summary>
    public void Mix(Span<float> mix)
    {
        mix.Clear();
        foreach (var input in _inputs)
            input.Buffer.ReadAdd(mix, input.Enabled ? input.Gain : 0f);
        for (var i = 0; i < mix.Length; i++)
            mix[i] = Math.Clamp(mix[i], -1f, 1f);
        WrittenFrames += mix.Length / 2;
    }

    /// <summary>Convierte una hora de la grabación (100 ns) en trama de audio.</summary>
    public long FrameAt(long timelineTicks) => timelineTicks * SampleRate / 10_000_000;

    /// <summary>Convierte una trama de audio en hora (100 ns), para el sello de cada muestra.</summary>
    public long TicksAt(long frame) => frame * 10_000_000 / SampleRate;
}
