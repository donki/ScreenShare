namespace SocScreenShare.Audio;

/// <summary>
/// Cuánta energía hay a una frecuencia concreta (algoritmo de Goertzel). Sirve para comprobar, en
/// las pruebas, que en el MP4 está el tono de la aplicación que se grababa y no el de otra.
/// </summary>
public static class ToneDetector
{
    /// <summary>Amplitud aproximada (0-1) de la componente a <paramref name="frequency"/> Hz en <paramref name="samples"/> (mono).</summary>
    public static double Amplitude(ReadOnlySpan<float> samples, int sampleRate, double frequency)
    {
        if (samples.IsEmpty || sampleRate <= 0)
            return 0;
        var w = 2 * Math.PI * frequency / sampleRate;
        var coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        foreach (var x in samples)
        {
            var s0 = x + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(0, power)) / samples.Length;
    }

    /// <summary>Estéreo intercalado a mono (media de los dos canales).</summary>
    public static float[] ToMono(ReadOnlySpan<float> stereo)
    {
        var mono = new float[stereo.Length / 2];
        for (var i = 0; i < mono.Length; i++)
            mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) / 2;
        return mono;
    }
}
