namespace SocScreenShare.Audio;

/// <summary>
/// Cambia la frecuencia de muestreo de un flujo estéreo intercalado por interpolación lineal,
/// paquete a paquete y sin cortes entre paquetes (guarda la última trama y la fase). Casi todos los
/// dispositivos de Windows ya trabajan a 48 kHz y entonces no hace nada; con 44,1 kHz o con un
/// micrófono de 16 kHz basta para voz y sonido del sistema.
/// </summary>
public sealed class LinearResampler
{
    private readonly double _step;
    private double _position;       // posición de la siguiente salida, en tramas de entrada; -1 = la trama guardada
    private float _lastL, _lastR;
    private bool _hasLast;

    public LinearResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        InputRate = inputRate;
        OutputRate = outputRate;
        _step = (double)inputRate / outputRate;
    }

    public int InputRate { get; }
    public int OutputRate { get; }
    public bool IsPassThrough => InputRate == OutputRate;

    /// <summary>Tramas de salida que pueden salir como mucho de <paramref name="inputFrames"/> de entrada.</summary>
    public int MaxOutputFrames(int inputFrames) => (int)Math.Ceiling((inputFrames + 1) / _step) + 1;

    /// <summary>Remuestrea <paramref name="input"/> (estéreo) y devuelve las tramas escritas en <paramref name="output"/>.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        var inFrames = input.Length / 2;
        if (IsPassThrough)
        {
            input[..(inFrames * 2)].CopyTo(output);
            return inFrames;
        }
        if (inFrames == 0)
            return 0;
        if (!_hasLast)
        {
            // Primer paquete: se empieza justo en su primera trama.
            _lastL = input[0];
            _lastR = input[1];
            _hasLast = true;
            _position = 0;
        }
        var written = 0;
        // Tramas disponibles: la guardada (índice -1) y las de este paquete (0..inFrames-1).
        while (_position <= inFrames - 1 && written * 2 + 1 < output.Length)
        {
            var i = (int)Math.Floor(_position);
            var frac = (float)(_position - i);
            float l0, r0;
            if (i < 0) { l0 = _lastL; r0 = _lastR; }
            else { l0 = input[i * 2]; r0 = input[i * 2 + 1]; }
            var n = i + 1;
            var l1 = n < inFrames ? input[n * 2] : l0;
            var r1 = n < inFrames ? input[n * 2 + 1] : r0;
            if (n >= inFrames && frac > 0)
                break;   // hace falta la siguiente trama, que vendrá en el próximo paquete
            output[written * 2] = l0 + (l1 - l0) * frac;
            output[written * 2 + 1] = r0 + (r1 - r0) * frac;
            written++;
            _position += _step;
        }
        _lastL = input[(inFrames - 1) * 2];
        _lastR = input[(inFrames - 1) * 2 + 1];
        _position -= inFrames;   // la última trama de este paquete pasa a ser la guardada (-1)
        return written;
    }

    public void Reset()
    {
        _hasLast = false;
        _position = 0;
    }
}
