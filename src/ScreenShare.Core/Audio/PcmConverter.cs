using System.Buffers.Binary;

namespace SocScreenShare.Audio;

/// <summary>Formato de un paquete de audio tal como lo entrega Windows (WASAPI).</summary>
public sealed record PcmFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    public int BytesPerFrame => Channels * BitsPerSample / 8;

    public bool IsSupported =>
        SampleRate > 0 && Channels > 0 &&
        (IsFloat ? BitsPerSample == 32 : BitsPerSample is 16 or 24 or 32);
}

/// <summary>
/// Pasa cualquier paquete de WASAPI (16, 24 o 32 bits enteros, o 32 bits en coma flotante; mono,
/// estéreo o más canales) a estéreo en coma flotante intercalado (L, R, L, R…). Con más de dos
/// canales se quedan el izquierdo y el derecho delanteros, que es donde va casi todo; en mono se
/// duplica.
/// </summary>
public static class PcmConverter
{
    /// <summary>Convierte y devuelve cuántas tramas (pares L/R) ha escrito en <paramref name="stereo"/>.</summary>
    public static int ToStereoFloat(ReadOnlySpan<byte> data, PcmFormat format, Span<float> stereo)
    {
        if (!format.IsSupported)
            throw new NotSupportedException($"Formato de audio no admitido: {format}.");
        var frames = Math.Min(data.Length / format.BytesPerFrame, stereo.Length / 2);
        var bytesPerSample = format.BitsPerSample / 8;
        for (var f = 0; f < frames; f++)
        {
            var frame = data.Slice(f * format.BytesPerFrame, format.BytesPerFrame);
            var left = Sample(frame, 0, bytesPerSample, format.IsFloat);
            var right = format.Channels > 1 ? Sample(frame, bytesPerSample, bytesPerSample, format.IsFloat) : left;
            stereo[f * 2] = left;
            stereo[f * 2 + 1] = right;
        }
        return frames;
    }

    public static int FramesIn(int byteCount, PcmFormat format) => byteCount / format.BytesPerFrame;

    private static float Sample(ReadOnlySpan<byte> frame, int offset, int size, bool isFloat)
    {
        var s = frame.Slice(offset, size);
        if (isFloat)
            return BinaryPrimitives.ReadSingleLittleEndian(s);
        return size switch
        {
            2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
            3 => ((s[2] << 24) | (s[1] << 16) | (s[0] << 8)) / 2147483648f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
        };
    }

    /// <summary>Estéreo intercalado a mono (media de los dos canales); devuelve las muestras escritas.</summary>
    public static int DownmixToMono(ReadOnlySpan<float> stereo, Span<float> mono)
    {
        var frames = Math.Min(stereo.Length / 2, mono.Length);
        for (var i = 0; i < frames; i++)
            mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
        return frames;
    }

    /// <summary>Estéreo en coma flotante a PCM de 16 bits (lo que acepta el codificador AAC de Windows), recortando lo que se pase de ±1.</summary>
    public static void ToPcm16(ReadOnlySpan<float> samples, Span<byte> pcm)
    {
        if (pcm.Length < samples.Length * 2)
            throw new ArgumentException("El destino es demasiado pequeño.", nameof(pcm));
        for (var i = 0; i < samples.Length; i++)
        {
            var v = Math.Clamp(samples[i], -1f, 1f);
            var s = (short)Math.Round(v < 0 ? v * 32768f : v * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(i * 2, 2), s);
        }
    }
}
