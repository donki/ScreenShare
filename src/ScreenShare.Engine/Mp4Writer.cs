using System.Runtime.InteropServices;
using SocScreenShare.Recording;
using SocScreenShare.Settings;
using Vortice.MediaFoundation;

namespace SocScreenShare.Engine;

/// <summary>
/// El fichero MP4 con Media Foundation (parte de Windows): vídeo H.264 (perfil High) o HEVC (Main)
/// y audio AAC a la frecuencia y con los canales del plan. Primero se prueba el codificador por
/// hardware de la tarjeta gráfica; si no hay o no acepta la configuración, el de software. El control
/// de tasa (CBR, VBR o calidad) se le pide al codificador; si no lo admite, se graba sin él y se dice
/// (<see cref="RateControlApplied"/>).
/// </summary>
internal sealed class Mp4Writer : IDisposable
{
    // Atributos que Vortice no trae con nombre.
    private static readonly Guid MtYuvMatrix = new("3E23D450-2C75-4D25-A00E-B91670D12327");
    private static readonly Guid MtNominalRange = new("C21B8EE5-B956-4071-8DAF-325EDF5CAB11");
    private static readonly Guid MtPrimaries = new("DBFBE4D7-0740-4EE0-8192-850AB0E21935");
    private static readonly Guid MtTransfer = new("5FB0FCE9-BE5C-4935-A811-EC838F8EED93");
    private static readonly Guid MtMaxKeyframeSpacing = new("C16EB52B-73A1-476F-8D62-839D6A020652");
    private static readonly Guid TranscodeContainerType = new("150FF23F-4ABC-478B-AC4F-E1916FBA1CCA");

    private const int InterlaceProgressive = 2;
    private const int H264ProfileHigh = 100;
    private const int H264ProfileMain = 77;
    private const int HevcProfileMain = 1;

    // Propiedades de ICodecAPI que el sink writer pasa al codificador (codecapi.h).
    private static readonly Guid CodecRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");
    private static readonly Guid CodecMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");
    private static readonly Guid CodecQuality = new("FCBF57A3-7EA5-4B0C-9644-69B40C39C391");

    private static int _started;

    private readonly IMFSinkWriter _writer;
    private readonly object _gate = new();
    private readonly int _videoStream;
    private readonly int _audioStream = -1;
    private readonly int _videoBytes;
    private bool _finished;

    public Mp4Writer(string path, EncodingPlan plan, bool withAudio, bool tryHardware = true)
    {
        Startup();
        Plan = plan;
        _videoBytes = plan.Width * plan.Height * 3 / 2;
        Exception? last = null;
        var profiles = plan.Codec == VideoCodec.Hevc ? new[] { HevcProfileMain } : new[] { H264ProfileHigh, H264ProfileMain };
        // Primero con el control de tasa pedido; si ningún codificador lo acepta, sin él.
        foreach (var withRate in new[] { true, false })
        {
            foreach (var hardware in new[] { true, false })
            {
                if (hardware && !tryHardware)
                    continue;
                foreach (var profile in profiles)
                {
                    IMFSinkWriter? writer = null;
                    try
                    {
                        TryDelete(path);
                        writer = Create(path, plan, withAudio, hardware, profile, withRate, out _videoStream, out _audioStream);
                        _writer = writer;
                        IsHardware = hardware;
                        RateControlApplied = withRate;
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        AppLog.Write($"codificador {plan.Codec} ({(hardware ? "hardware" : "software")}, perfil {profile}, control de tasa {(withRate ? plan.RateControl : "por defecto")}) no disponible: {ex.Message}");
                        writer?.Dispose();
                    }
                }
            }
        }
        TryDelete(path);
        throw new InvalidOperationException($"No hay codificador {plan.Codec} disponible en este equipo.", last);
    }

    public EncodingPlan Plan { get; }
    public bool IsHardware { get; }

    /// <summary>El codificador aceptó el control de tasa pedido (si no, usa el suyo por defecto).</summary>
    public bool RateControlApplied { get; }
    public bool HasAudio => _audioStream >= 0;
    public long VideoSamples { get; private set; }
    public long AudioSamples { get; private set; }

    private static IMFSinkWriter Create(string path, EncodingPlan plan, bool withAudio, bool hardware, int profile, bool withRate, out int videoStream, out int audioStream)
    {
        using var attrs = MediaFactory.MFCreateAttributes(4);
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, hardware ? 1u : 0u);
        attrs.Set(TranscodeContainerType, TranscodeContainerTypeGuids.Mpeg4);
        // Sin «índice al principio» (MF_MPEG4SINK_MOOV_BEFORE_MDAT): con él, Media Foundation deja la
        // pista AAC sin poderse decodificar (comprobado el 2026-10-12 con Mp4Probe).
        using var stream = MediaFactory.MFCreateFile(FileAccessMode.MfAccessModeReadwrite, FileOpenMode.MfOpenModeDeleteIfExist, FileFlags.None, path);
        var writer = MediaFactory.MFCreateSinkWriterFromURL(path, stream, attrs);
        try
        {
            // ---- vídeo
            using (var outType = MediaFactory.MFCreateMediaType())
            {
                outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outType.Set(MediaTypeAttributeKeys.Subtype, plan.Codec == VideoCodec.Hevc ? VideoFormatGuids.Hevc : VideoFormatGuids.H264);
                outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)plan.VideoBitrate);
                outType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)InterlaceProgressive);
                outType.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)plan.Width, (uint)plan.Height));
                outType.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(plan.Fps, 1));
                outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
                outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)profile);
                outType.Set(MtMaxKeyframeSpacing, (uint)(plan.Fps * 2));
                SetColor(outType);
                videoStream = writer.AddStream(outType);
            }
            using (var inType = MediaFactory.MFCreateMediaType())
            {
                inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                inType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)InterlaceProgressive);
                inType.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)plan.Width, (uint)plan.Height));
                inType.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(plan.Fps, 1));
                inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
                inType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)plan.Width);
                SetColor(inType);
                using var encoding = withRate ? RateParameters(plan) : null;
                writer.SetInputMediaType(videoStream, inType, encoding!);
            }

            // ---- audio
            audioStream = -1;
            if (withAudio)
            {
                using (var outType = MediaFactory.MFCreateMediaType())
                {
                    outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                    outType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    outType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)plan.AudioSampleRate);
                    outType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)plan.AudioChannels);
                    outType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)plan.AudioBytesPerSecond);
                    outType.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                    outType.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
                    audioStream = writer.AddStream(outType);
                }
                using (var inType = MediaFactory.MFCreateMediaType())
                {
                    inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                    inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)plan.AudioSampleRate);
                    inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)plan.AudioChannels);
                    inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(plan.AudioChannels * 2));
                    inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(plan.AudioSampleRate * plan.AudioChannels * 2));
                    writer.SetInputMediaType(audioStream, inType, null!);
                }
            }
            writer.BeginWriting();
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// eAVEncCommonRateControlMode: 0 CBR, 2 VBR sin tope (con la media pedida), 3 calidad (0-100).
    /// </summary>
    private static IMFAttributes RateParameters(EncodingPlan plan)
    {
        var a = MediaFactory.MFCreateAttributes(3);
        switch (plan.RateControl)
        {
            case RateControl.Cbr:
                a.Set(CodecRateControlMode, 0u);
                a.Set(CodecMeanBitRate, (uint)plan.VideoBitrate);
                break;
            case RateControl.Quality:
                a.Set(CodecRateControlMode, 3u);
                a.Set(CodecQuality, (uint)Math.Clamp(plan.QualityLevel, 0, 100));
                break;
            default:
                a.Set(CodecRateControlMode, 2u);
                a.Set(CodecMeanBitRate, (uint)plan.VideoBitrate);
                break;
        }
        return a;
    }

    /// <summary>BT.709 en rango limitado, lo mismo que produce la conversión de color.</summary>
    private static void SetColor(IMFMediaType type)
    {
        type.Set(MtYuvMatrix, 1u);       // MFVideoTransferMatrix_BT709
        type.Set(MtNominalRange, 2u);    // MFNominalRange_16_235
        type.Set(MtPrimaries, 2u);       // MFVideoPrimaries_BT709
        type.Set(MtTransfer, 5u);        // MFVideoTransFunc_709
    }

    public void WriteVideo(ReadOnlySpan<byte> nv12, long time, long duration)
    {
        if (nv12.Length < _videoBytes)
            throw new ArgumentException("Imagen incompleta.", nameof(nv12));
        Write(_videoStream, nv12[.._videoBytes], time, duration);
        VideoSamples++;
    }

    public void WriteAudio(ReadOnlySpan<byte> pcm16, long time, long duration)
    {
        if (_audioStream < 0 || pcm16.IsEmpty)
            return;
        Write(_audioStream, pcm16, time, duration);
        AudioSamples++;
    }

    private void Write(int stream, ReadOnlySpan<byte> data, long time, long duration)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out var ptr, out _, out _);
        try
        {
            unsafe { data.CopyTo(new Span<byte>((void*)ptr, data.Length)); }
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = data.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        lock (_gate)
        {
            if (_finished)
                return;
            _writer.WriteSample(stream, sample);
        }
    }

    /// <summary>Cierra el fichero (escribe el índice). Sin esto el MP4 no se puede abrir.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_finished)
                return;
            _finished = true;
            _writer.Finalize();
        }
    }

    public void Dispose()
    {
        _writer.Dispose();
    }

    private static void Startup()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            MediaFactory.MFStartup(false).CheckError();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    /// <summary>Para el registro: por qué Media Foundation rechazó algo.</summary>
    public static string Describe(Exception ex) => ex is COMException c ? $"0x{c.HResult:X8} {c.Message}" : ex.Message;
}
