using System.Globalization;
using Vortice.MediaFoundation;

namespace SocScreenShare.Engine;

/// <summary>Lo que hay dentro de un MP4, leído con Media Foundation (como lo leerá un reproductor).</summary>
public sealed record ProbeResult(
    TimeSpan Duration,
    bool HasVideo,
    int Width,
    int Height,
    double Fps,
    long VideoSamples,
    bool HasAudio,
    int AudioRate,
    int AudioChannels,
    long AudioSamples,
    float AudioPeak,
    string VideoCodec = "",
    int VideoKbps = 0,
    int AudioKbps = 0)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"duration={Duration.TotalSeconds:0.00}s video={(HasVideo ? $"{VideoCodec} {Width}x{Height}@{Fps:0.##} {VideoKbps}kbps samples={VideoSamples}" : "no")} " +
        $"audio={(HasAudio ? $"AAC {AudioRate}Hz/{AudioChannels}ch {AudioKbps}kbps samples={AudioSamples} peak={AudioPeak:0.000}" : "no")}");
}

/// <summary>
/// Comprueba un MP4 de verdad: lo abre, mira sus pistas y decodifica todas las muestras (el audio a
/// PCM para medir su pico). Lo usan la línea de órdenes (<c>--probe</c>, y tras <c>--record</c>) y las
/// pruebas de integración.
/// </summary>
public static class Mp4Probe
{
    private static readonly Guid PdDuration = new("6C990D33-BB8E-477A-8598-0D5D96FCD88A");
    private const int FirstVideo = unchecked((int)0xFFFFFFFC);
    private const int FirstAudio = unchecked((int)0xFFFFFFFD);
    private const int AnyStream = unchecked((int)0xFFFFFFFE);
    private const int MediaSource = unchecked((int)0xFFFFFFFF);

    public static ProbeResult Probe(string path) => Probe(path, null);

    /// <summary>El audio decodificado (estéreo intercalado a la frecuencia de la pista), para las comprobaciones de tono.</summary>
    public static (float[] Stereo, int Rate) ReadAudio(string path)
    {
        var all = new List<float>();
        var p = Probe(path, all);
        return ([.. all], p.AudioRate);
    }

    private static ProbeResult Probe(string path, List<float>? keep)
    {
        MediaFactory.MFStartup(false).CheckError();
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);

        var duration = TimeSpan.Zero;
        try
        {
            var v = reader.GetPresentationAttribute(MediaSource, PdDuration);
            duration = TimeSpan.FromTicks(System.Convert.ToInt64(v.Value, CultureInfo.InvariantCulture));
        }
        catch (Exception) { }

        bool hasVideo = false, hasAudio = false;
        int width = 0, height = 0, rate = 0, channels = 0, audioKbps = 0;
        var codec = string.Empty;
        double fps = 0;
        try
        {
            using var vt = reader.GetNativeMediaType(FirstVideo, 0);
            hasVideo = true;
            var sub = vt.GetGUID(MediaTypeAttributeKeys.Subtype);
            codec = sub == VideoFormatGuids.H264 ? "H264" : sub == VideoFormatGuids.Hevc ? "HEVC" : sub.ToString();
            MediaFactory.UnpackSize(vt.GetUInt64(MediaTypeAttributeKeys.FrameSize), out var w, out var h);
            MediaFactory.UnpackRatio(vt.GetUInt64(MediaTypeAttributeKeys.FrameRate), out var num, out var den);
            (width, height, fps) = ((int)w, (int)h, den == 0 ? 0 : (double)num / den);
        }
        catch (Exception) { }
        try
        {
            using var at = reader.GetNativeMediaType(FirstAudio, 0);
            hasAudio = true;
            rate = (int)at.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
            channels = (int)at.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
            try { audioKbps = (int)(at.GetUInt32(MediaTypeAttributeKeys.AudioAvgBytesPerSecond) * 8 / 1000); } catch (Exception) { }
            // El audio se decodifica a PCM en coma flotante para medir su pico.
            using var pcm = MediaFactory.MFCreateMediaType();
            pcm.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            pcm.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float);
            reader.SetCurrentMediaType(FirstAudio, pcm);
        }
        catch (Exception) { }

        long videoSamples = 0, audioSamples = 0, videoBytes = 0;
        var peak = 0f;
        var audioIndex = -1;
        var videoIndex = -1;
        var ended = new HashSet<int>();
        var expected = (hasVideo ? 1 : 0) + (hasAudio ? 1 : 0);
        while (ended.Count < expected)
        {
            using var sample = reader.ReadSample(AnyStream, 0, out var stream, out var flags, out _);
            if ((flags & SourceReaderFlag.Error) != 0)
                break;
            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                // Cada pista acaba por su lado: se sigue hasta que acaben todas.
                if (!ended.Add(stream))
                    break;
                continue;
            }
            if (sample is null)
                continue;
            if (videoIndex < 0 && hasVideo && IsMajor(reader, stream, MediaTypeGuids.Video)) videoIndex = stream;
            if (audioIndex < 0 && hasAudio && IsMajor(reader, stream, MediaTypeGuids.Audio)) audioIndex = stream;
            if (stream == videoIndex)
            {
                videoSamples++;
                videoBytes += sample.TotalLength;
            }
            else if (stream == audioIndex)
            {
                audioSamples++;
                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out var ptr, out _, out var len);
                try
                {
                    unsafe
                    {
                        var floats = new ReadOnlySpan<float>((void*)ptr, len / 4);
                        foreach (var f in floats)
                            peak = Math.Max(peak, Math.Abs(f));
                        keep?.AddRange(floats.ToArray());
                    }
                }
                finally
                {
                    buffer.Unlock();
                }
            }
        }
        var videoKbps = duration.TotalSeconds > 0 ? (int)Math.Round(videoBytes * 8 / duration.TotalSeconds / 1000) : 0;
        return new ProbeResult(duration, hasVideo, width, height, fps, videoSamples, hasAudio, rate, channels, audioSamples, peak, codec, videoKbps, audioKbps);
    }

    private static bool IsMajor(IMFSourceReader reader, int stream, Guid major)
    {
        try
        {
            using var t = reader.GetCurrentMediaType(stream);
            return t.GetGUID(MediaTypeAttributeKeys.MajorType) == major;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
