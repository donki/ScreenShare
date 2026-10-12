namespace SocScreenShare.Recording;

/// <summary>Las calidades que se ofrecen: dos atajos (que rellenan las opciones) y la personalizada.</summary>
public enum QualityPreset
{
    Share,
    High,
    Custom,
}

public enum VideoCodec
{
    H264,
    Hevc,
}

/// <summary>Cómo reparte el codificador los bits.</summary>
public enum RateControl
{
    /// <summary>Tasa constante: el tamaño se sabe de antemano.</summary>
    Cbr,

    /// <summary>Tasa variable con la media pedida: mejor imagen con el mismo tamaño medio.</summary>
    Vbr,

    /// <summary>Calidad constante (0-100): el tamaño depende de lo que se grabe.</summary>
    Quality,
}

/// <summary>Resolución máxima (el lado corto; se respeta la proporción y nunca se agranda).</summary>
public enum MaxResolution
{
    Original,
    P2160,
    P1440,
    P1080,
    P720,
    P480,
}

/// <summary>
/// Las opciones de salida de audio y vídeo. Los dos atajos («Óptima para compartir» y «Alta») son
/// valores concretos de estas opciones; cualquier cambio a mano las convierte en «Personalizada».
/// </summary>
public sealed class QualityOptions
{
    public static readonly int[] FpsChoices = [15, 24, 30, 60];
    public static readonly int[] AudioKbpsChoices = [96, 128, 160, 192, 256, 320];

    /// <summary>El codificador AAC de Windows solo admite 12000, 16000, 20000 y 24000 bytes/s.</summary>
    public static readonly int[] SupportedAudioKbps = [96, 128, 160, 192];

    public static readonly int[] SampleRates = [44100, 48000];
    public const double MinMbps = 0.2;
    public const double MaxMbps = 100;

    public MaxResolution Resolution { get; set; } = MaxResolution.P1080;
    public int Fps { get; set; } = 30;
    public bool AutoBitrate { get; set; } = true;
    public double VideoMbps { get; set; } = 4;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public RateControl RateControl { get; set; } = RateControl.Vbr;

    /// <summary>Calidad (0-100) cuando <see cref="RateControl"/> es <see cref="RateControl.Quality"/>.</summary>
    public int QualityLevel { get; set; } = 70;

    public int AudioKbps { get; set; } = 128;
    public int AudioSampleRate { get; set; } = 48000;
    public int AudioChannels { get; set; } = 2;

    public static bool IsAacSupported(int kbps) => SupportedAudioKbps.Contains(kbps);

    public static QualityOptions ForPreset(QualityPreset preset) => preset == QualityPreset.High
        ? new QualityOptions { Resolution = MaxResolution.P2160, Fps = 60, AudioKbps = 192 }
        : new QualityOptions();

    /// <summary>¿Coinciden con un atajo? Si no, son personalizadas.</summary>
    public QualityPreset Detect() =>
        SameAs(ForPreset(QualityPreset.Share)) ? QualityPreset.Share
        : SameAs(ForPreset(QualityPreset.High)) ? QualityPreset.High
        : QualityPreset.Custom;

    public bool SameAs(QualityOptions o) =>
        Resolution == o.Resolution && Fps == o.Fps && AutoBitrate == o.AutoBitrate && (AutoBitrate || Math.Abs(VideoMbps - o.VideoMbps) < 0.001)
        && Codec == o.Codec && RateControl == o.RateControl && (RateControl != RateControl.Quality || QualityLevel == o.QualityLevel)
        && AudioKbps == o.AudioKbps && AudioSampleRate == o.AudioSampleRate && AudioChannels == o.AudioChannels;

    public QualityOptions Clone() => (QualityOptions)MemberwiseClone();

    /// <summary>Corrige lo que no valga, al valor admitido más cercano.</summary>
    public QualityOptions Normalize()
    {
        if (!Enum.IsDefined(Resolution)) Resolution = MaxResolution.P1080;
        if (!Enum.IsDefined(Codec)) Codec = VideoCodec.H264;
        if (!Enum.IsDefined(RateControl)) RateControl = RateControl.Vbr;
        Fps = Nearest(FpsChoices, Fps);
        VideoMbps = double.IsFinite(VideoMbps) ? Math.Clamp(Math.Round(VideoMbps, 1), MinMbps, MaxMbps) : 4;
        QualityLevel = Math.Clamp(QualityLevel, 0, 100);
        AudioKbps = Nearest(SupportedAudioKbps, AudioKbps);
        AudioSampleRate = Nearest(SampleRates, AudioSampleRate);
        AudioChannels = AudioChannels <= 1 ? 1 : 2;
        return this;
    }

    /// <summary>El códec que se usará: HEVC solo si el equipo tiene codificador; si no, H.264.</summary>
    public VideoCodec EffectiveCodec(bool hevcAvailable) => Codec == VideoCodec.Hevc && !hevcAvailable ? VideoCodec.H264 : Codec;

    /// <summary>La caja de la resolución máxima en horizontal (ancho, alto).</summary>
    public static (int Width, int Height) Box(MaxResolution r) => r switch
    {
        MaxResolution.P2160 => (3840, 2160),
        MaxResolution.P1440 => (2560, 1440),
        MaxResolution.P1080 => (1920, 1080),
        MaxResolution.P720 => (1280, 720),
        MaxResolution.P480 => (854, 480),
        // Original, hasta el máximo de los codificadores de Windows (H.264: 4096×2304).
        _ => (4096, 2304),
    };

    private static int Nearest(int[] choices, int value) => choices.OrderBy(c => Math.Abs(c - value)).ThenBy(c => c).First();
}

/// <summary>
/// Lo que se va a codificar: tamaño de salida, imágenes por segundo, bitrates, códec y audio.
/// Siempre MP4 con H.264 (o HEVC) y AAC, que es lo que aceptan móviles, navegadores y mensajería.
/// </summary>
public sealed record EncodingPlan(
    int Width,
    int Height,
    int Fps,
    int VideoBitrate,
    int AudioBitrate,
    VideoCodec Codec = VideoCodec.H264,
    RateControl RateControl = RateControl.Vbr,
    int QualityLevel = 70,
    int AudioSampleRate = 48000,
    int AudioChannels = 2)
{
    /// <summary>Bytes por segundo de AAC (el codificador de Windows solo admite 12000, 16000, 20000 y 24000).</summary>
    public int AudioBytesPerSecond => AudioBitrate / 8;

    /// <summary>Tamaño aproximado del fichero por minuto (vídeo + audio, sin contar la cabecera).</summary>
    public long BytesPerMinute(bool withAudio) => (VideoBitrate + (withAudio ? AudioBitrate : 0)) / 8L * 60;
}

/// <summary>
/// Cálculo del plan a partir de lo que se graba y de las opciones (General §8.6: lógica fuera de la
/// interfaz). Nunca se agranda la imagen: una ventana pequeña sale a su tamaño, con bandas negras
/// hasta <see cref="MinSide"/> píxeles por lado si es más pequeña aún (los codificadores no admiten menos).
/// <para>Bitrate automático: unos 0,065 bits por píxel en H.264 (≈ 4 Mbit/s a 1080p30) y 0,04 en
/// HEVC, que comprime mejor; a más de 30 imágenes/s, un 25 % menos por imagen (cambian menos entre
/// sí). Entre 0,5 y 40 Mbit/s.</para>
/// </summary>
public static class Presets
{
    public const int MinSide = 128;
    public const int MinAutoBitrate = 500_000;
    public const int MaxAutoBitrate = 40_000_000;

    public static EncodingPlan For(QualityPreset preset, int sourceWidth, int sourceHeight) =>
        For(QualityOptions.ForPreset(preset), sourceWidth, sourceHeight);

    public static EncodingPlan For(QualityOptions q, int sourceWidth, int sourceHeight)
    {
        var (boxW, boxH) = QualityOptions.Box(q.Resolution);
        var landscape = sourceWidth >= sourceHeight;
        var (w, h) = Fit(sourceWidth, sourceHeight, landscape ? boxW : boxH, landscape ? boxH : boxW);
        (w, h) = (Math.Max(w, MinSide), Math.Max(h, MinSide));
        var bitrate = q.AutoBitrate
            ? AutoBitrate(w, h, q.Fps, q.Codec)
            : (int)Math.Round(Math.Clamp(q.VideoMbps, QualityOptions.MinMbps, QualityOptions.MaxMbps) * 1_000_000);
        return new EncodingPlan(w, h, q.Fps, bitrate, q.AudioKbps * 1000, q.Codec, q.RateControl, q.QualityLevel, q.AudioSampleRate, q.AudioChannels);
    }

    public static int AutoBitrate(int width, int height, int fps, VideoCodec codec)
    {
        var bpp = codec == VideoCodec.Hevc ? 0.04 : 0.065;
        if (fps > 30)
            bpp *= 0.75;
        return VideoBitrate(width, height, fps, bpp, MinAutoBitrate, MaxAutoBitrate);
    }

    /// <summary>El tamaño que cabe en la caja sin deformar ni agrandar, con lados pares (lo exige H.264 en 4:2:0).</summary>
    public static (int Width, int Height) Fit(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "El origen no tiene tamaño.");
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight));
        var w = Even((int)Math.Round(sourceWidth * scale));
        var h = Even((int)Math.Round(sourceHeight * scale));
        return (Math.Min(w, Even(maxWidth)), Math.Min(h, Even(maxHeight)));
    }

    public static int VideoBitrate(int width, int height, int fps, double bitsPerPixel, int min, int max)
    {
        var bits = (long)Math.Round((double)width * height * fps * bitsPerPixel);
        return (int)Math.Clamp(bits, min, max);
    }

    private static int Even(int value) => Math.Max(2, value & ~1);
}
