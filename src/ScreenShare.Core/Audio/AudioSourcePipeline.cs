using SocScreenShare.Recording;

namespace SocScreenShare.Audio;

/// <summary>
/// Lo que le pasa a cada paquete de un origen de audio desde que Windows lo entrega hasta que queda
/// en la mezcla: a estéreo en coma flotante, a la frecuencia de la mezcla, colocado en su hora de la grabación (o
/// descartado si es de antes de empezar o de una pausa). Un origen, un hilo de captura: no es seguro
/// entre hilos por sí mismo, pero sí lo es el búfer en el que escribe.
/// </summary>
public sealed class AudioSourcePipeline
{
    private readonly PcmFormat _format;
    private readonly RecordingClock _clock;
    private readonly MixerInput _input;
    private readonly AudioMixer _mixer;
    private readonly LinearResampler _resampler;
    private readonly SourcePlacement _placement;
    private float[] _stereo = [];
    private float[] _resampled = [];
    private bool _wasDiscarding = true;

    public AudioSourcePipeline(PcmFormat format, RecordingClock clock, AudioMixer mixer, MixerInput input)
    {
        if (!format.IsSupported)
            throw new NotSupportedException($"Formato de audio no admitido: {format}.");
        _format = format;
        _clock = clock;
        _input = input;
        _mixer = mixer;
        _resampler = new LinearResampler(format.SampleRate, mixer.SampleRate);
        _placement = new SourcePlacement(mixer.SampleRate / 50);   // 20 ms
    }

    public PcmFormat Format => _format;

    /// <summary>Paquetes recibidos (para el registro y las pruebas).</summary>
    public long Packets { get; private set; }

    /// <summary>Pico de la última señal recibida (0-1), para un medidor.</summary>
    public float LastPeak { get; private set; }

    /// <param name="data">Los bytes del paquete en <see cref="Format"/>.</param>
    /// <param name="silent">Windows marca el paquete como silencio: se toma como ceros.</param>
    /// <param name="captureTime">Hora de captura de la primera trama, en 100 ns del QPC; 0 si Windows no la da.</param>
    public void OnPacket(ReadOnlySpan<byte> data, bool silent, long captureTime)
    {
        Packets++;
        var frames = PcmConverter.FramesIn(data.Length, _format);
        if (frames == 0)
            return;
        if (captureTime <= 0)
            captureTime = _clock.Now() - (long)frames * 10_000_000 / _format.SampleRate;
        var timeline = _clock.TimelineAt(captureTime);
        if (timeline is null)
        {
            // Antes de empezar o en pausa: no se graba, y al volver se recoloca en su hora.
            _wasDiscarding = true;
            LastPeak = 0;
            return;
        }
        if (_wasDiscarding)
        {
            _placement.Reset();
            _resampler.Reset();
            _wasDiscarding = false;
        }

        Ensure(ref _stereo, frames * 2);
        var stereo = _stereo.AsSpan(0, frames * 2);
        if (silent)
            stereo.Clear();
        else
            PcmConverter.ToStereoFloat(data, _format, stereo);

        var outCapacity = _resampler.MaxOutputFrames(frames) * 2;
        Ensure(ref _resampled, outCapacity);
        var outFrames = _resampler.Process(stereo, _resampled.AsSpan(0, outCapacity));
        var output = _resampled.AsSpan(0, outFrames * 2);

        var peak = 0f;
        foreach (var s in output)
            peak = Math.Max(peak, Math.Abs(s));
        LastPeak = peak;

        var start = _placement.Place(_mixer.FrameAt(timeline.Value), outFrames);
        _input.Buffer.Write(start, output);
    }

    private static void Ensure(ref float[] buffer, int length)
    {
        if (buffer.Length < length)
            buffer = new float[length];
    }
}
